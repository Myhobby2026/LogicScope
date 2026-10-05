#include "dma_sampler.h"

#include <DMAChannel.h>
#include <imxrt.h>

#include "pins.h"

namespace LogicScope {
namespace {

constexpr uint8_t kSlotEmpty = 0;
constexpr uint8_t kSlotGpio6Done = 1;
constexpr uint8_t kSlotGpio7Done = 2;
constexpr uint8_t kSlotReady = 3;
constexpr uint8_t kSlotBusy = 4;
constexpr uint32_t kTcdSize32Bit = DMA_TCD_ATTR_SIZE_32BIT;

using Tcd = DMABaseClass::TCD_t;

// OCRAM is DMA-visible on RT1062. The paired raw ring consumes 256 KiB; a
// 16K-sample x four-slot ring would consume the entire 512 KiB OCRAM before
// the descriptors and Teensy USB buffers are placed there.
DMAMEM static uint32_t g_gpio6[kDmaBlockCount][kDmaBlockSamples]
    __attribute__((aligned(32)));
DMAMEM static uint32_t g_gpio7[kDmaBlockCount][kDmaBlockSamples]
    __attribute__((aligned(32)));
DMAMEM static Tcd g_tcd6[kDmaBlockCount] __attribute__((aligned(32)));
DMAMEM static Tcd g_tcd7[kDmaBlockCount] __attribute__((aligned(32)));

static DMAChannel g_dma6;
static DMAChannel g_dma7;
static volatile uint8_t g_slotState[kDmaBlockCount] = {};
static volatile uint8_t g_writeIndex6 = 0;
static volatile uint8_t g_writeIndex7 = 0;
static uint8_t g_consumeIndex = 0;
static volatile bool g_running = false;
static volatile bool g_overrun = false;
static volatile uint32_t g_overrunCount = 0;
static bool g_initialized = false;

static void connectXbar(uint16_t input, uint16_t output) {
  // XBARA1 selectors are 16-bit registers, two output selectors per register.
  // This is the same packed selector layout used by the Teensy 4.x core.
  volatile uint16_t* selector = &XBARA1_SEL0 + (output >> 1);
  const uint16_t oldValue = *selector;
  if ((output & 1u) == 0u) {
    *selector = static_cast<uint16_t>((oldValue & 0xFF00u) | input);
  } else {
    *selector = static_cast<uint16_t>((oldValue & 0x00FFu) | (input << 8));
  }
}

static void fillTcdRing(Tcd* ring, volatile uint32_t* destination,
                        volatile const void* source) {
  for (uint8_t i = 0; i < kDmaBlockCount; ++i) {
    Tcd& tcd = ring[i];
    // One triggered minor loop performs exactly one aligned 32-bit GPIO read.
    tcd.SADDR = source;
    tcd.SOFF = 0;
    tcd.ATTR = static_cast<uint16_t>((kTcdSize32Bit << 8) | kTcdSize32Bit);
    tcd.NBYTES = sizeof(uint32_t);
    tcd.SLAST = 0;
    tcd.DADDR = &destination[static_cast<uint32_t>(i) * kDmaBlockSamples];
    tcd.DOFF = sizeof(uint32_t);
    tcd.CITER = kDmaBlockSamples;
    // ESG loads the next 32-byte TCD after this block; it avoids software
    // re-arming and therefore avoids a CPU-created gap between DMA blocks.
    tcd.DLASTSGA = static_cast<int32_t>(
        reinterpret_cast<uintptr_t>(&ring[(i + 1u) % kDmaBlockCount]));
    tcd.CSR = DMA_TCD_CSR_ESG | DMA_TCD_CSR_INTMAJOR;
    tcd.BITER = kDmaBlockSamples;
  }
}

static void copyTcdToHardware(DMAChannel& channel, const Tcd& source) {
  // TCD registers are memory-mapped volatile fields. Copy field-by-field so
  // the compiler cannot replace writes with an ordinary cached memcpy.
  channel.TCD->SADDR = source.SADDR;
  channel.TCD->SOFF = source.SOFF;
  channel.TCD->ATTR = source.ATTR;
  channel.TCD->NBYTES = source.NBYTES;
  channel.TCD->SLAST = source.SLAST;
  channel.TCD->DADDR = source.DADDR;
  channel.TCD->DOFF = source.DOFF;
  channel.TCD->CITER = source.CITER;
  channel.TCD->DLASTSGA = source.DLASTSGA;
  channel.TCD->CSR = source.CSR;
  channel.TCD->BITER = source.BITER;
}

static void stopFromIsr() {
  // PIT is the pacing source. Disable it first so no new XBAR/DMAMUX request is
  // generated while the foreground code reports the overrun.
  PIT_TCTRL0 = 0;
  g_dma6.disable();
  g_dma7.disable();
  g_running = false;
  g_overrun = true;
  ++g_overrunCount;
}

static void markBlockComplete(uint8_t bank) {
  const uint8_t slot = bank == 6 ? g_writeIndex6 : g_writeIndex7;
  if (bank == 6) {
    g_writeIndex6 = static_cast<uint8_t>((g_writeIndex6 + 1u) % kDmaBlockCount);
  } else {
    g_writeIndex7 = static_cast<uint8_t>((g_writeIndex7 + 1u) % kDmaBlockCount);
  }

  const uint8_t state = g_slotState[slot];
  if (state == kSlotEmpty) {
    g_slotState[slot] = bank == 6 ? kSlotGpio6Done : kSlotGpio7Done;
  } else if ((bank == 6 && state == kSlotGpio7Done) ||
             (bank == 7 && state == kSlotGpio6Done)) {
    g_slotState[slot] = kSlotReady;
  } else {
    // A READY/BUSY/duplicate half means the producer has reached a slot the
    // consumer did not release. Stop sampling and make loss explicit.
    stopFromIsr();
    __DMB();
    return;
  }

  // ESG has already loaded the next TCD, but the next PIT request has not
  // normally arrived yet. Look one block ahead: if the next destination still
  // belongs to the consumer (or is an unconsumed half-pair), stop before the
  // DMA ring starts overwriting it.
  const uint8_t nextSlot = bank == 6 ? g_writeIndex6 : g_writeIndex7;
  if (g_slotState[nextSlot] != kSlotEmpty) stopFromIsr();
  __DMB();
}

static void dma6Isr() {
  g_dma6.clearInterrupt();
  if (g_running) markBlockComplete(6);
}

static void dma7Isr() {
  g_dma7.clearInterrupt();
  if (g_running) markBlockComplete(7);
}

}  // namespace

bool dmaSamplerInit() {
  if (g_initialized) return true;
  if (!gpioBankMapMatchesCore()) return false;

  for (uint8_t i = 0; i < 16; ++i) pinMode(kCapturePins[i], INPUT);

  // Gate the PIT and XBAR clocks. PIT is free-running; its channel interrupt
  // is deliberately disabled, because the channel's trigger output feeds DMA.
  CCM_CCGR1 |= CCM_CCGR1_PIT(CCM_CCGR_ON);
  CCM_CCGR2 |= CCM_CCGR2_XBAR1(CCM_CCGR_ON);
  PIT_MCR = 0;
  PIT_TCTRL0 = 0;
  PIT_TFLG0 = PIT_TFLG_TIF;  // W1C: clear a stale timer flag before arming.

  connectXbar(XBARA1_IN_PIT_TRIGGER0, XBARA1_OUT_DMA_CH_MUX_REQ30);
  // XBAR output 0 is configured as an edge-qualified DMA request. The PIT
  // trigger is a periodic pulse; rising-edge mode emits one request per tick.
  XBARA1_CTRL0 = XBARA_CTRL_STS0 | XBARA_CTRL_EDGE0(1) | XBARA_CTRL_DEN0;

  fillTcdRing(g_tcd6, g_gpio6[0], reinterpret_cast<volatile const void*>(&GPIO6_DR));
  fillTcdRing(g_tcd7, g_gpio7[0], reinterpret_cast<volatile const void*>(&GPIO7_DR));
  // TCDs live in cacheable OCRAM. Flush descriptors before eDMA follows ESG
  // links; the DMA engine does not snoop the Cortex-M7 data cache.
  arm_dcache_flush(const_cast<Tcd*>(g_tcd6), sizeof(g_tcd6));
  arm_dcache_flush(const_cast<Tcd*>(g_tcd7), sizeof(g_tcd7));

  g_dma6.triggerAtHardwareEvent(DMAMUX_SOURCE_XBAR1_0);
  g_dma7.triggerAtHardwareEvent(DMAMUX_SOURCE_XBAR1_0);
  g_dma6.attachInterrupt(dma6Isr, 32);
  g_dma7.attachInterrupt(dma7Isr, 32);
  g_initialized = true;
  return true;
}

bool dmaSamplerStart(uint32_t requestedRateHz, uint32_t maxRateHz,
                     uint32_t& actualRateHz) {
  if (!g_initialized || requestedRateHz == 0 || requestedRateHz > maxRateHz) {
    return false;
  }
  dmaSamplerStop();

  // PIT clock is the 150 MHz IPG/F_BUS clock on the standard Teensy 4.1 clock
  // tree. Integer reload values quantize the request; report the derived rate
  // to firmware state, and the host derives the same value from this clock.
  const uint64_t timerHz = F_BUS_ACTUAL;
  uint64_t divider = (timerHz + (requestedRateHz / 2u)) / requestedRateHz;
  if (divider < 1u) divider = 1u;
  if (divider > 0x100000000ULL) return false;
  actualRateHz = static_cast<uint32_t>(timerHz / divider);

  noInterrupts();
  for (uint8_t i = 0; i < kDmaBlockCount; ++i) g_slotState[i] = kSlotEmpty;
  g_writeIndex6 = 0;
  g_writeIndex7 = 0;
  g_consumeIndex = 0;
  g_overrun = false;
  interrupts();

  g_dma6.disable();
  g_dma7.disable();
  g_dma6.clearInterrupt();
  g_dma7.clearInterrupt();
  copyTcdToHardware(g_dma6, g_tcd6[0]);
  copyTcdToHardware(g_dma7, g_tcd7[0]);
  __DSB();
  g_dma6.enable();
  g_dma7.enable();

  PIT_TCTRL0 = 0;
  PIT_LDVAL0 = static_cast<uint32_t>(divider - 1u);
  PIT_TFLG0 = PIT_TFLG_TIF;  // W1C again immediately before the capture.
  g_running = true;
  __DSB();
  PIT_TCTRL0 = PIT_TCTRL_TEN;  // TEN only: no PIT CPU interrupt is enabled.
  return true;
}

void dmaSamplerStop() {
  PIT_TCTRL0 = 0;
  if (g_initialized) {
    g_dma6.disable();
    g_dma7.disable();
  }
  g_running = false;
  __DSB();
}

bool dmaSamplerTryAcquire(RawBlockView& view) {
  uint8_t slot = 0xFF;
  noInterrupts();
  if (g_slotState[g_consumeIndex] == kSlotReady) {
    slot = g_consumeIndex;
    g_slotState[slot] = kSlotBusy;
    g_consumeIndex = static_cast<uint8_t>((g_consumeIndex + 1u) % kDmaBlockCount);
  }
  interrupts();
  if (slot == 0xFF) return false;

  // DMA writes bypass D-cache. Invalidate exactly the completed slot before
  // CPU loads; aligned block boundaries keep invalidation from touching peers.
  arm_dcache_delete(g_gpio6[slot], sizeof(g_gpio6[slot]));
  arm_dcache_delete(g_gpio7[slot], sizeof(g_gpio7[slot]));
  __DMB();
  view.gpio6 = g_gpio6[slot];
  view.gpio7 = g_gpio7[slot];
  view.samples = kDmaBlockSamples;
  view.slot = slot;
  return true;
}

void dmaSamplerRelease(uint8_t slot) {
  if (slot >= kDmaBlockCount) return;
  noInterrupts();
  if (g_slotState[slot] == kSlotBusy) g_slotState[slot] = kSlotEmpty;
  interrupts();
}

bool dmaSamplerIsRunning() { return g_running; }

bool dmaSamplerTakeOverrun() {
  noInterrupts();
  const bool value = g_overrun;
  g_overrun = false;
  interrupts();
  return value;
}

uint32_t dmaSamplerOverrunCount() { return g_overrunCount; }

}  // namespace LogicScope
