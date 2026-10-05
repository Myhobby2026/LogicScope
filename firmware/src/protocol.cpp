#include "protocol.h"

#ifndef LOGICSCOPE_FIRMWARE_VERSION_MAJOR
#define LOGICSCOPE_FIRMWARE_VERSION_MAJOR 1
#define LOGICSCOPE_FIRMWARE_VERSION_MINOR 0
#define LOGICSCOPE_FIRMWARE_VERSION_PATCH 0
#endif

namespace LogicScope {
namespace {

uint8_t g_opcode = 0;
uint8_t g_payload[8] = {};
uint8_t g_payloadRead = 0;
bool g_waitingForStartPayload = false;

uint32_t readU32Le(const uint8_t* bytes) {
  return static_cast<uint32_t>(bytes[0]) |
         (static_cast<uint32_t>(bytes[1]) << 8) |
         (static_cast<uint32_t>(bytes[2]) << 16) |
         (static_cast<uint32_t>(bytes[3]) << 24);
}

}  // namespace

void protocolInit() {
  g_opcode = 0;
  g_payloadRead = 0;
  g_waitingForStartPayload = false;
}

bool protocolPoll(HostCommand& command) {
  while (Serial.available() > 0) {
    const int value = Serial.read();
    if (value < 0) return false;
    const uint8_t byte = static_cast<uint8_t>(value);

    if (!g_waitingForStartPayload) {
      g_opcode = byte;
      if (g_opcode == 0x01) {
        g_payloadRead = 0;
        g_waitingForStartPayload = true;
        continue;
      }
      command = HostCommand{};
      switch (g_opcode) {
        case 0x00:
          command.kind = HostCommandKind::Stop;
          return true;
        case 0x02:
          command.kind = HostCommandKind::GetCaps;
          return true;
        case 0x03:
          command.kind = HostCommandKind::SelfTest;
          return true;
        default:
          command.kind = HostCommandKind::Invalid;
          return true;
      }
    }

    g_payload[g_payloadRead++] = byte;
    if (g_payloadRead == sizeof(g_payload)) {
      g_waitingForStartPayload = false;
      command = HostCommand{};
      command.kind = HostCommandKind::Start;
      command.rateHz = readU32Le(g_payload);
      command.mode = g_payload[4];
      command.triggerChannel = g_payload[5];
      command.triggerEdge = g_payload[6];
      command.preTriggerPercent = g_payload[7];
      return true;
    }
  }
  return false;
}

void protocolSendCaps(uint32_t maxBurstRateHz, uint32_t maxStreamRateHz,
                      uint32_t ramSamples) {
  uint8_t caps[16] = {};
  const uint32_t values[3] = {maxBurstRateHz, maxStreamRateHz, ramSamples};
  for (uint8_t field = 0; field < 3; ++field) {
    const uint32_t value = values[field];
    const uint8_t offset = static_cast<uint8_t>(field * 4u);
    caps[offset] = static_cast<uint8_t>(value);
    caps[offset + 1] = static_cast<uint8_t>(value >> 8);
    caps[offset + 2] = static_cast<uint8_t>(value >> 16);
    caps[offset + 3] = static_cast<uint8_t>(value >> 24);
  }
  caps[12] = LOGICSCOPE_FIRMWARE_VERSION_MAJOR;
  caps[13] = LOGICSCOPE_FIRMWARE_VERSION_MINOR;
  caps[14] = LOGICSCOPE_FIRMWARE_VERSION_PATCH;
  caps[15] = 16;
  Serial.write(caps, sizeof(caps));
}

void protocolSendStatus(uint8_t state, uint8_t lastError) {
  const uint8_t response[3] = {0x55, state, lastError};
  Serial.write(response, sizeof(response));
}

}  // namespace LogicScope
