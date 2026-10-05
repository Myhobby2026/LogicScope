#include "trigger.h"

namespace LogicScope {

void TriggerEngine::reset() {
  kind_ = TriggerKind::None;
  edge_ = TriggerEdge::Rising;
  channel_ = 0xFF;
  window_ = 1;
  consecutive_ = 0;
  patternMask_ = 0;
  patternValue_ = 0;
  previous_ = 0;
  havePrevious_ = false;
  fired_ = false;
}

bool TriggerEngine::configureEdge(uint8_t channel, TriggerEdge edge) {
  if (channel != 0xFF && channel >= 16) return false;
  reset();
  if (channel == 0xFF) return true;
  kind_ = TriggerKind::Edge;
  channel_ = channel;
  edge_ = edge;
  return true;
}

bool TriggerEngine::configurePattern(uint16_t mask, uint16_t value,
                                     uint8_t matchWindowSamples) {
  if (matchWindowSamples == 0 || matchWindowSamples > 16) return false;
  reset();
  if (mask == 0) return true;
  kind_ = TriggerKind::Pattern;
  patternMask_ = mask;
  patternValue_ = static_cast<uint16_t>(value & mask);
  window_ = matchWindowSamples;
  return true;
}

bool TriggerEngine::observe(uint16_t sample) {
  if (fired_) return true;

  if (kind_ == TriggerKind::Edge && havePrevious_) {
    const uint16_t bit = static_cast<uint16_t>(1u << channel_);
    const bool wasHigh = (previous_ & bit) != 0;
    const bool isHigh = (sample & bit) != 0;
    if ((edge_ == TriggerEdge::Rising && !wasHigh && isHigh) ||
        (edge_ == TriggerEdge::Falling && wasHigh && !isHigh)) {
      fired_ = true;
    }
  } else if (kind_ == TriggerKind::Pattern) {
    if ((sample & patternMask_) == patternValue_) {
      if (consecutive_ < window_) ++consecutive_;
      if (consecutive_ >= window_) fired_ = true;
    } else {
      consecutive_ = 0;
    }
  }

  previous_ = sample;
  havePrevious_ = true;
  return fired_;
}

}  // namespace LogicScope
