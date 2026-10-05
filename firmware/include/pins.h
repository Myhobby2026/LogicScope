#pragma once

#include <Arduino.h>

#include "channel_map.h"

#if !defined(__IMXRT1062__) || !defined(ARDUINO_TEENSY41)
#error "LogicScope firmware requires the Teensy 4.1 / i.MX RT1062 Arduino core."
#endif

namespace LogicScope {

// Compile-time checks bind the logical pin/bit table to the installed
// Teensyduino core's authoritative CORE_PIN*_BIT constants. This catches a
// core/board variant mismatch at compile time instead of silently permuting
// channels. GPIO bank/address checks are performed once at startup below.
static_assert(CORE_PIN0_BIT == 3 && CORE_PIN1_BIT == 2 &&
                  CORE_PIN14_BIT == 18 && CORE_PIN15_BIT == 19,
              "Teensy pin 0/1/14/15 GPIO6 bit mapping changed");
static_assert(CORE_PIN18_BIT == 17 && CORE_PIN19_BIT == 16 &&
                  CORE_PIN20_BIT == 26 && CORE_PIN21_BIT == 27,
              "Teensy pin 18/19/20/21 GPIO6 bit mapping changed");
static_assert(CORE_PIN6_BIT == 10 && CORE_PIN7_BIT == 17 &&
                  CORE_PIN8_BIT == 16 && CORE_PIN9_BIT == 11 &&
                  CORE_PIN10_BIT == 0 && CORE_PIN11_BIT == 2 &&
                  CORE_PIN12_BIT == 1 && CORE_PIN13_BIT == 3,
              "Teensy pin 6..13 GPIO7 bit mapping changed");
static_assert(kChannelPinMap[4].gpioBit == CORE_PIN18_BIT &&
                  kChannelPinMap[5].gpioBit == CORE_PIN19_BIT,
              "Logical channel map must match Teensy core pin bits");

constexpr uint8_t kCorePinBits[16] = {
    CORE_PIN0_BIT, CORE_PIN1_BIT, CORE_PIN14_BIT, CORE_PIN15_BIT,
    CORE_PIN18_BIT, CORE_PIN19_BIT, CORE_PIN20_BIT, CORE_PIN21_BIT,
    CORE_PIN6_BIT, CORE_PIN7_BIT, CORE_PIN8_BIT, CORE_PIN9_BIT,
    CORE_PIN10_BIT, CORE_PIN11_BIT, CORE_PIN12_BIT, CORE_PIN13_BIT};
constexpr uint8_t kCorePinBanks[16] = {
    6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7};
constexpr bool channelMapMatchesCore() {
  for (uint8_t channel = 0; channel < 16; ++channel) {
    if (kChannelPinMap[channel].arduinoPin != kCapturePins[channel] ||
        kChannelPinMap[channel].gpioBank != kCorePinBanks[channel] ||
        kChannelPinMap[channel].gpioBit != kCorePinBits[channel])
      return false;
  }
  return true;
}
static_assert(channelMapMatchesCore(),
              "Logical channel map must match every Teensy core pin bit");

inline bool gpioBankMapMatchesCore() {
  // This is a one-time address check, not a sample read. CORE_PIN*_PORTREG
  // names the actual fast GPIO register chosen by the Arduino core.
  return &CORE_PIN0_PORTREG == &GPIO6_DR &&
         &CORE_PIN1_PORTREG == &GPIO6_DR &&
         &CORE_PIN14_PORTREG == &GPIO6_DR &&
         &CORE_PIN15_PORTREG == &GPIO6_DR &&
         &CORE_PIN18_PORTREG == &GPIO6_DR &&
         &CORE_PIN19_PORTREG == &GPIO6_DR &&
         &CORE_PIN20_PORTREG == &GPIO6_DR &&
         &CORE_PIN21_PORTREG == &GPIO6_DR &&
         &CORE_PIN6_PORTREG == &GPIO7_DR &&
         &CORE_PIN7_PORTREG == &GPIO7_DR &&
         &CORE_PIN8_PORTREG == &GPIO7_DR &&
         &CORE_PIN9_PORTREG == &GPIO7_DR &&
         &CORE_PIN10_PORTREG == &GPIO7_DR &&
         &CORE_PIN11_PORTREG == &GPIO7_DR &&
         &CORE_PIN12_PORTREG == &GPIO7_DR &&
         &CORE_PIN13_PORTREG == &GPIO7_DR;
}

}  // namespace LogicScope
