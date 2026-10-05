#include <Arduino.h>

#include "dma_sampler.h"
#include "pins.h"
#include "protocol.h"
#include "trigger.h"
#include "usb_tx.h"

namespace LogicScope {
namespace {

constexpr uint8_t kModeStreaming = 0;
constexpr uint8_t kModeBurst = 1;
constexpr uint32_t kMaxBurstRateHz = 20'000'000;
constexpr uint32_t kMaxStreamRateHz = 10'000'000;
constexpr uint32_t kBurstCapacitySamples = 131072;
constexpr uint8_t kTestOutput0 = 22;
constexpr uint8_t kTestOutput1 = 23;

// State byte values are part of protocol.md.
enum class DeviceState : uint8_t {
  Idle = 0,
  Streaming = 1,
  BurstCapture = 2,
  BurstUpload = 3,
  SelfTest = 4,
};

enum ErrorCode : uint8_t {
  ErrorNone = 0,
  ErrorBadCommand = 1,
  ErrorInvalidConfig = 2,
  ErrorDmaOverrun = 3,
  ErrorBusy = 4,
  ErrorUsb = 5,
};

DeviceState g_state = DeviceState::Idle;
uint8_t g_captureMode = kModeStreaming;
uint16_t g_streamBlock[kDmaBlockSamples] __attribute__((aligned(32)));
// Packed capture storage is in normal tightly-coupled RAM (CPU-only after
// block packing); DMA raw pairs remain in DMAMEM OCRAM.
uint16_t g_burstSamples[kBurstCapacitySamples];
TriggerEngine g_trigger;

bool g_selfTestActive = false;
bool g_dmaReady = false;
bool g_transportError = false;
bool g_captureHadOverrun = false;
bool g_triggerEnabled = false;
bool g_triggered = false;
bool g_burstComplete = false;
uint32_t g_preTriggerTarget = 0;
uint32_t g_preTriggerCount = 0;
uint32_t g_burstWritePosition = 0;
uint32_t g_burstStartPosition = 0;
uint32_t g_burstSampleCount = 0;
uint32_t g_postTriggerTarget = 0;
uint32_t g_postTriggerCount = 0;

uint32_t ringAdvance(uint32_t index, uint32_t count) {
  return (index + count) % kBurstCapacitySamples;
}

void resetBurstRecorder(uint8_t triggerChannel, uint8_t edge,
                        uint8_t preTriggerPercent) {
  g_preTriggerTarget = (kBurstCapacitySamples * preTriggerPercent) / 100u;
  g_preTriggerCount = 0;
  g_burstWritePosition = 0;
  g_burstStartPosition = 0;
  g_burstSampleCount = 0;
  g_postTriggerTarget = 0;
  g_postTriggerCount = 0;
  g_triggered = false;
  g_burstComplete = false;
  g_triggerEnabled = triggerChannel != 0xFF;
  if (!g_trigger.configureEdge(triggerChannel,
                               edge == 0 ? TriggerEdge::Falling
                                         : TriggerEdge::Rising)) {
    g_triggerEnabled = false;
  }
}

void burstObserveSample(uint16_t sample) {
  if (g_burstComplete) return;

  if (!g_triggerEnabled) {
    // No firmware trigger: start at the first sample and fill linearly.
    if (g_burstSampleCount < kBurstCapacitySamples) {
      g_burstSamples[g_burstSampleCount++] = sample;
    }
    if (g_burstSampleCount == kBurstCapacitySamples) g_burstComplete = true;
    return;
  }

  if (!g_triggered) {
    if (g_trigger.observe(sample)) {
      g_triggered = true;
      // `writePosition` points to the next slot after the retained pre-trigger
      // history. Starting here produces a chronological, possibly wrapped view.
      g_burstStartPosition =
          (g_burstWritePosition + kBurstCapacitySamples - g_preTriggerCount) %
          kBurstCapacitySamples;
      g_burstSampleCount = g_preTriggerCount;
      g_postTriggerTarget = kBurstCapacitySamples - g_preTriggerTarget;
      if (g_postTriggerTarget == 0) {
        // 100% pre-trigger intentionally contains no post-trigger sample.
        g_burstComplete = true;
        return;
      }
      g_burstSamples[g_burstWritePosition] = sample;
      g_burstWritePosition = ringAdvance(g_burstWritePosition, 1);
      ++g_burstSampleCount;
      ++g_postTriggerCount;
      if (g_postTriggerCount >= g_postTriggerTarget) g_burstComplete = true;
      return;
    }

    // Keep only the most recent requested pre-trigger history. If the trigger
    // arrives before this fills, the eventual capture is shorter, not padded.
    if (g_preTriggerTarget != 0) {
      g_burstSamples[g_burstWritePosition] = sample;
      g_burstWritePosition = ringAdvance(g_burstWritePosition, 1);
      if (g_preTriggerCount < g_preTriggerTarget) ++g_preTriggerCount;
    }
    return;
  }

  if (g_postTriggerCount < g_postTriggerTarget) {
    g_burstSamples[g_burstWritePosition] = sample;
    g_burstWritePosition = ringAdvance(g_burstWritePosition, 1);
    ++g_burstSampleCount;
    ++g_postTriggerCount;
    if (g_postTriggerCount >= g_postTriggerTarget) g_burstComplete = true;
  }
}

void stopSelfTest() {
  if (!g_selfTestActive) return;
  analogWrite(kTestOutput0, 0);
  analogWrite(kTestOutput1, 0);
  pinMode(kTestOutput0, INPUT);
  pinMode(kTestOutput1, INPUT);
  g_selfTestActive = false;
  g_state = DeviceState::Idle;
}

void startSelfTest() {
  pinMode(kTestOutput0, OUTPUT);
  pinMode(kTestOutput1, OUTPUT);
  analogWriteResolution(8);
  analogWriteFrequency(kTestOutput0, 1'000'000);
  analogWriteFrequency(kTestOutput1, 1'000'000);
  analogWrite(kTestOutput0, 128);
  analogWrite(kTestOutput1, 128);
  g_selfTestActive = true;
  g_state = DeviceState::SelfTest;
}

void sendStatus(uint8_t error) {
  protocolSendStatus(static_cast<uint8_t>(g_state), error);
}

void beginCapture(const HostCommand& command) {
  const bool selfTestStandby = g_state == DeviceState::SelfTest && g_selfTestActive;
  if (g_state != DeviceState::Idle && !selfTestStandby) {
    sendStatus(ErrorBusy);
    return;
  }
  if (!g_dmaReady || command.mode > kModeBurst || command.rateHz == 0 ||
      command.triggerEdge > 1 || command.preTriggerPercent > 100 ||
      (command.triggerChannel != 0xFF && command.triggerChannel >= 16)) {
    sendStatus(ErrorInvalidConfig);
    return;
  }

  const uint32_t maxRate = command.mode == kModeBurst
                               ? kMaxBurstRateHz
                               : kMaxStreamRateHz;
  g_captureMode = command.mode;
  g_transportError = false;
  g_captureHadOverrun = false;
  usbTxResetSequence();

  // Publish all foreground state before enabling PIT. The first DMA block can
  // complete quickly at high rates, so the loop must never see stale mode or
  // trigger state from the preceding capture.
  if (g_captureMode == kModeBurst) {
    resetBurstRecorder(command.triggerChannel, command.triggerEdge,
                       command.preTriggerPercent);
    g_state = DeviceState::BurstCapture;
  } else {
    g_trigger.reset();
    g_triggerEnabled = false;  // Streaming trigger decisions are host-side.
    g_state = DeviceState::Streaming;
  }

  uint32_t actualRate = 0;
  if (!dmaSamplerStart(command.rateHz, maxRate, actualRate) || actualRate == 0) {
    g_state = g_selfTestActive ? DeviceState::SelfTest : DeviceState::Idle;
    sendStatus(ErrorInvalidConfig);
    return;
  }
  sendStatus(ErrorNone);
}

void requestStop() {
  if (g_state == DeviceState::SelfTest) {
    stopSelfTest();
    sendStatus(ErrorNone);
    return;
  }
  if (g_state == DeviceState::Streaming || g_state == DeviceState::BurstCapture) {
    // Drain any already-completed paired blocks in loop() before finalizing.
    dmaSamplerStop();
    return;
  }
  if (g_state != DeviceState::Idle) sendStatus(ErrorNone);
}

void handleCommand(const HostCommand& command) {
  switch (command.kind) {
    case HostCommandKind::Start:
      beginCapture(command);
      break;
    case HostCommandKind::Stop:
      requestStop();
      break;
    case HostCommandKind::GetCaps:
      if (g_state == DeviceState::Idle || g_state == DeviceState::SelfTest) {
        protocolSendCaps(g_dmaReady ? kMaxBurstRateHz : 0,
                         g_dmaReady ? kMaxStreamRateHz : 0,
                         g_dmaReady ? kBurstCapacitySamples : 0);
      } else {
        sendStatus(ErrorBusy);
      }
      break;
    case HostCommandKind::SelfTest:
      if (g_state == DeviceState::Idle || g_state == DeviceState::SelfTest) {
        if (g_selfTestActive) stopSelfTest();
        else startSelfTest();
        sendStatus(ErrorNone);
      } else {
        sendStatus(ErrorBusy);
      }
      break;
    case HostCommandKind::Invalid:
    default:
      sendStatus(ErrorBadCommand);
      break;
  }
}

bool streamBlock(const RawBlockView& block) {
  for (uint16_t i = 0; i < block.samples; ++i) {
    g_streamBlock[i] = packLogicalSample(block.gpio6[i], block.gpio7[i]);
  }
  return usbTxSendSamples(g_streamBlock, block.samples, kModeStreaming,
                          g_selfTestActive ? kDataFlagSelfTest : 0, false);
}

void processBurstBlock(const RawBlockView& block) {
  for (uint16_t i = 0; i < block.samples && !g_burstComplete; ++i) {
    const uint16_t sample = packLogicalSample(block.gpio6[i], block.gpio7[i]);
    burstObserveSample(sample);
  }
}

bool sendBurstWindow(bool overrun) {
  g_state = DeviceState::BurstUpload;
  sendStatus(overrun ? ErrorDmaOverrun : ErrorNone);
  uint8_t flags = 0;
  if (g_triggered) flags |= kDataFlagTriggered;
  if (overrun) flags |= kDataFlagOverrun;
  if (g_selfTestActive) flags |= kDataFlagSelfTest;

  if (g_burstSampleCount == 0) return usbTxSendEmptyFinal(kModeBurst, flags);

  uint32_t emitted = 0;
  while (emitted < g_burstSampleCount) {
    const uint32_t index = ringAdvance(g_burstStartPosition, emitted);
    const uint32_t contiguous = min(kBurstCapacitySamples - index,
                                    g_burstSampleCount - emitted);
    const bool finalPart = emitted + contiguous == g_burstSampleCount;
    if (!usbTxSendSamples(&g_burstSamples[index], contiguous, kModeBurst,
                          flags, finalPart)) {
      return false;
    }
    emitted += contiguous;
  }
  return true;
}

void finalizeBurst() {
  dmaSamplerStop();
  g_captureHadOverrun = g_captureHadOverrun || dmaSamplerTakeOverrun();
  if (g_triggerEnabled && !g_triggered) {
    // STOP/overrun before an edge returns the retained pre-trigger history in
    // chronological order rather than stale buffer contents from its start.
    g_burstStartPosition =
        (g_burstWritePosition + kBurstCapacitySamples - g_preTriggerCount) %
        kBurstCapacitySamples;
    g_burstSampleCount = g_preTriggerCount;
  }
  const bool sent = sendBurstWindow(g_captureHadOverrun);
  g_state = g_selfTestActive ? DeviceState::SelfTest : DeviceState::Idle;
  if (!sent) {
    g_transportError = true;
    sendStatus(ErrorUsb);
  } else {
    sendStatus(g_captureHadOverrun ? ErrorDmaOverrun : ErrorNone);
  }
}

void finalizeStreaming() {
  dmaSamplerStop();
  g_captureHadOverrun = g_captureHadOverrun || dmaSamplerTakeOverrun();
  g_state = g_selfTestActive ? DeviceState::SelfTest : DeviceState::Idle;
  if (g_transportError) {
    sendStatus(ErrorUsb);
  } else if (g_captureHadOverrun) {
    sendStatus(ErrorDmaOverrun);
  } else {
    sendStatus(ErrorNone);
  }
}

}  // namespace
}  // namespace LogicScope

using namespace LogicScope;

void setup() {
  Serial.begin(115200);  // USB CDC; baud is ignored by the native USB device.
  protocolInit();
  // Do not emit unsolicited bytes: GET_CAPS is an unframed, fixed-size reply
  // and a boot status could be mistaken for its first three capability bytes.
  g_dmaReady = dmaSamplerInit();
}

void loop() {
  HostCommand command;
  while (protocolPoll(command)) handleCommand(command);

  RawBlockView block;
  while (dmaSamplerTryAcquire(block)) {
    if (g_state == DeviceState::Streaming) {
      if (!streamBlock(block)) {
        g_transportError = true;
            dmaSamplerStop();
      }
    } else if (g_state == DeviceState::BurstCapture && !g_burstComplete) {
      processBurstBlock(block);
    }
    dmaSamplerRelease(block.slot);

    if (g_state == DeviceState::BurstCapture && g_burstComplete) {
      finalizeBurst();
      break;
    }
  }

  if (g_state == DeviceState::BurstCapture && g_burstComplete) {
    finalizeBurst();
  } else if ((g_state == DeviceState::Streaming ||
              g_state == DeviceState::BurstCapture) &&
             !dmaSamplerIsRunning()) {
    g_captureHadOverrun = g_captureHadOverrun || dmaSamplerTakeOverrun();
    if (g_state == DeviceState::BurstCapture) {
      // A user STOP or DMA overrun uploads the already retained, chronological
      // portion (possibly just the pre-trigger history).
      finalizeBurst();
    } else {
      finalizeStreaming();
    }
  }
}
