#include <cassert>
#include <cstdint>

#include "trigger.h"

using LogicScope::TriggerEdge;
using LogicScope::TriggerEngine;

int main() {
  TriggerEngine trigger;
  assert(trigger.configureEdge(3, TriggerEdge::Rising));
  assert(!trigger.observe(0x0000));
  assert(trigger.observe(static_cast<uint16_t>(1u << 3)));
  assert(trigger.observe(static_cast<uint16_t>(1u << 3)));
  assert(trigger.observe(0)); // A fired trigger remains latched until reconfigured.

  assert(trigger.configureEdge(15, TriggerEdge::Falling));
  assert(!trigger.observe(0));
  assert(!trigger.observe(static_cast<uint16_t>(1u << 15)));
  assert(trigger.observe(0));

  assert(!trigger.configureEdge(16, TriggerEdge::Rising));
  assert(trigger.configureEdge(0xFF, TriggerEdge::Rising));
  assert(!trigger.observe(0xFFFF)); // Disabled trigger.

  assert(trigger.configurePattern(0x00F0, 0x12F3, 2));
  assert(!trigger.observe(0x12A0));
  assert(!trigger.observe(0x12F0)); // The first of two consecutive matches.
  assert(!trigger.observe(0x1200)); // A mismatch resets the window.
  assert(!trigger.observe(0x12F7));
  assert(trigger.observe(0x12F1));

  assert(trigger.configurePattern(0, 0, 1)); // An empty mask disables pattern triggering.
  assert(!trigger.observe(0));
  assert(!trigger.configurePattern(0xFFFF, 0, 0));
  assert(!trigger.configurePattern(0xFFFF, 0, 17));
  return 0;
}
