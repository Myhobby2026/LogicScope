#include <cassert>
#include <cstdint>

#include "channel_map.h"

using LogicScope::kChannelPinMap;
using LogicScope::packLogicalSample;
using LogicScope::uint8_t;
using LogicScope::uint16_t;
using LogicScope::uint32_t;

int main() {
  assert(packLogicalSample(0, 0) == 0);
  assert(packLogicalSample(0xFFFFFFFFu, 0xFFFFFFFFu) == 0xFFFF);

  for (uint8_t channel = 0; channel < 16; ++channel) {
    const auto mapping = kChannelPinMap[channel];
    const uint32_t bankWord = uint32_t{1} << mapping.gpioBit;
    const uint16_t expected = static_cast<uint16_t>(uint16_t{1} << channel);
    const uint16_t sample = mapping.gpioBank == 6
        ? packLogicalSample(bankWord, 0)
        : packLogicalSample(0, bankWord);
    assert(sample == expected);
  }

  // Explicit regression check: pin 18 maps to GPIO6 bit 17; pin 19 maps to 16.
  assert(kChannelPinMap[4].arduinoPin == 18 && kChannelPinMap[4].gpioBit == 17);
  assert(kChannelPinMap[5].arduinoPin == 19 && kChannelPinMap[5].gpioBit == 16);
  return 0;
}
