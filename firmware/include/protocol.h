#pragma once

#include <Arduino.h>

namespace LogicScope {

enum class HostCommandKind : uint8_t { Start, Stop, GetCaps, SelfTest, Invalid };

struct HostCommand {
  HostCommandKind kind = HostCommandKind::Invalid;
  uint32_t rateHz = 0;
  uint8_t mode = 0;
  uint8_t triggerChannel = 0xFF;
  uint8_t triggerEdge = 1;
  uint8_t preTriggerPercent = 0;
};

void protocolInit();
bool protocolPoll(HostCommand& command);
void protocolSendCaps(uint32_t maxBurstRateHz, uint32_t maxStreamRateHz,
                      uint32_t ramSamples);
void protocolSendStatus(uint8_t state, uint8_t lastError);

}  // namespace LogicScope
