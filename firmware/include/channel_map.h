#pragma once

#include <cstdint>

namespace LogicScope {

using std::uint8_t;
using std::uint16_t;
using std::uint32_t;

struct ChannelPinMap {
  uint8_t arduinoPin;
  uint8_t gpioBank;
  uint8_t gpioBit;
};

// Logical channel order is the wire/UI order, not physical GPIO bit order.
// Teensy pin 18 is GPIO6 bit 17 and pin 19 is GPIO6 bit 16.
constexpr ChannelPinMap kChannelPinMap[16] = {
    {0, 6, 3},   {1, 6, 2},   {14, 6, 18}, {15, 6, 19},
    {18, 6, 17}, {19, 6, 16}, {20, 6, 26}, {21, 6, 27},
    {6, 7, 10},  {7, 7, 17},  {8, 7, 16},  {9, 7, 11},
    {10, 7, 0},  {11, 7, 2},  {12, 7, 1},  {13, 7, 3},
};

constexpr uint8_t kCapturePins[16] = {
    0, 1, 14, 15, 18, 19, 20, 21, 6, 7, 8, 9, 10, 11, 12, 13};

constexpr uint16_t packLogicalSample(uint32_t gpio6, uint32_t gpio7) {
  uint16_t value = 0;
  value |= static_cast<uint16_t>((gpio6 >> 3) & 1u) << 0;
  value |= static_cast<uint16_t>((gpio6 >> 2) & 1u) << 1;
  value |= static_cast<uint16_t>((gpio6 >> 18) & 1u) << 2;
  value |= static_cast<uint16_t>((gpio6 >> 19) & 1u) << 3;
  value |= static_cast<uint16_t>((gpio6 >> 17) & 1u) << 4;
  value |= static_cast<uint16_t>((gpio6 >> 16) & 1u) << 5;
  value |= static_cast<uint16_t>((gpio6 >> 26) & 1u) << 6;
  value |= static_cast<uint16_t>((gpio6 >> 27) & 1u) << 7;
  value |= static_cast<uint16_t>((gpio7 >> 10) & 1u) << 8;
  value |= static_cast<uint16_t>((gpio7 >> 17) & 1u) << 9;
  value |= static_cast<uint16_t>((gpio7 >> 16) & 1u) << 10;
  value |= static_cast<uint16_t>((gpio7 >> 11) & 1u) << 11;
  value |= static_cast<uint16_t>((gpio7 >> 0) & 1u) << 12;
  value |= static_cast<uint16_t>((gpio7 >> 2) & 1u) << 13;
  value |= static_cast<uint16_t>((gpio7 >> 1) & 1u) << 14;
  value |= static_cast<uint16_t>((gpio7 >> 3) & 1u) << 15;
  return value;
}

static_assert(kChannelPinMap[0].arduinoPin == kCapturePins[0] &&
                  kChannelPinMap[15].arduinoPin == kCapturePins[15],
              "Capture pin table and logical channel map must stay aligned");

}  // namespace LogicScope
