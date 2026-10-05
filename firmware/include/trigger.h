#pragma once

#include <cstdint>

namespace LogicScope {

using std::uint8_t;
using std::uint16_t;

enum class TriggerEdge : uint8_t { Falling = 0, Rising = 1 };
enum class TriggerKind : uint8_t { None, Edge, Pattern };

class TriggerEngine {
 public:
  void reset();
  bool configureEdge(uint8_t channel, TriggerEdge edge);
  // Firmware extension point for a mask/value trigger. `matchWindowSamples`
  // requires that masked pattern to be present for N consecutive samples.
  bool configurePattern(uint16_t mask, uint16_t value,
                        uint8_t matchWindowSamples);
  bool observe(uint16_t sample);
  bool fired() const { return fired_; }
  TriggerKind kind() const { return kind_; }

 private:
  TriggerKind kind_ = TriggerKind::None;
  TriggerEdge edge_ = TriggerEdge::Rising;
  uint8_t channel_ = 0xFF;
  uint8_t window_ = 1;
  uint8_t consecutive_ = 0;
  uint16_t patternMask_ = 0;
  uint16_t patternValue_ = 0;
  uint16_t previous_ = 0;
  bool havePrevious_ = false;
  bool fired_ = false;
};

}  // namespace LogicScope
