#pragma once

#include <Arduino.h>

namespace LogicScope {

constexpr uint32_t kMaxPacketSamples = 2048;  // 4 KiB sample payload per packet
constexpr uint8_t kDataFlagFinal = 1u << 0;
constexpr uint8_t kDataFlagTriggered = 1u << 1;
constexpr uint8_t kDataFlagOverrun = 1u << 2;
constexpr uint8_t kDataFlagSelfTest = 1u << 3;

void usbTxResetSequence();
bool usbTxSendSamples(const uint16_t* samples, uint32_t count, uint8_t mode,
                      uint8_t flags, bool finalPacket);
bool usbTxSendEmptyFinal(uint8_t mode, uint8_t flags);

}  // namespace LogicScope
