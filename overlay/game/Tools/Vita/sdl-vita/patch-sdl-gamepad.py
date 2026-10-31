#!/usr/bin/env python3
"""Add a local one-pass gamepad snapshot helper to SDL3 for the Vita build.

Usage: patch-sdl-gamepad.py <SDL source dir>
"""
import sys
from pathlib import Path

SIGNATURE = "bool SDL_GetGamepadButton(SDL_Gamepad *gamepad, SDL_GamepadButton button)"
MARKER = "void VitaSDL_GetGamepadSnapshot(SDL_Gamepad *gamepad, int32_t *out)"

HELPER = r"""/* Vita port: resolve the current mapping once for FNA's gamepad state snapshot. The public raw
 * joystick getters retain SDL's range checks and Vita backend mapping; the outer lock keeps the
 * binding list stable for the pass. */
static bool SDL_VitaResolveGamepadSnapshot(SDL_Gamepad *gamepad, int32_t *out)
{
    bool axis_resolved[SDL_GAMEPAD_AXIS_COUNT] = { false };
    int i;
    uint32_t buttons = 0;

    SDL_LockJoysticks();
    CHECK_GAMEPAD_MAGIC(gamepad, false);

    for (i = 0; i < gamepad->num_bindings; ++i) {
        const SDL_GamepadBinding *binding = &gamepad->bindings[i];

        if (binding->output_type == SDL_GAMEPAD_BINDTYPE_BUTTON) {
            int output_button = (int) binding->output.button;
            bool pressed = false;

            if (output_button < 0 || output_button >= 32) {
                continue;
            }
            if (binding->input_type == SDL_GAMEPAD_BINDTYPE_AXIS) {
                bool valid_input_range;
                int value = SDL_GetJoystickAxis(gamepad->joystick, binding->input.axis.axis);
                int threshold = binding->input.axis.axis_min + (binding->input.axis.axis_max - binding->input.axis.axis_min) / 2;
                if (binding->input.axis.axis_min < binding->input.axis.axis_max) {
                    valid_input_range = (value >= binding->input.axis.axis_min && value <= binding->input.axis.axis_max);
                    if (valid_input_range) {
                        pressed = (value >= threshold);
                    }
                } else {
                    valid_input_range = (value >= binding->input.axis.axis_max && value <= binding->input.axis.axis_min);
                    if (valid_input_range) {
                        pressed = (value <= threshold);
                    }
                }
            } else if (binding->input_type == SDL_GAMEPAD_BINDTYPE_BUTTON) {
                pressed = SDL_GetJoystickButton(gamepad->joystick, binding->input.button);
            } else if (binding->input_type == SDL_GAMEPAD_BINDTYPE_HAT) {
                int hat_mask = SDL_GetJoystickHat(gamepad->joystick, binding->input.hat.hat);
                pressed = ((hat_mask & binding->input.hat.hat_mask) != 0);
            }
            if (pressed) {
                buttons |= (1U << output_button);
            }
        } else if (binding->output_type == SDL_GAMEPAD_BINDTYPE_AXIS) {
            int output_axis = (int) binding->output.axis.axis;
            int value = 0;
            bool valid_output_range;

            if (output_axis < 0 || output_axis >= SDL_GAMEPAD_AXIS_COUNT || axis_resolved[output_axis]) {
                continue;
            }
            if (binding->input_type == SDL_GAMEPAD_BINDTYPE_AXIS) {
                bool valid_input_range;
                value = SDL_GetJoystickAxis(gamepad->joystick, binding->input.axis.axis);
                if (binding->input.axis.axis_min < binding->input.axis.axis_max) {
                    valid_input_range = (value >= binding->input.axis.axis_min && value <= binding->input.axis.axis_max);
                } else {
                    valid_input_range = (value >= binding->input.axis.axis_max && value <= binding->input.axis.axis_min);
                }
                if (valid_input_range) {
                    if (binding->input.axis.axis_min != binding->output.axis.axis_min || binding->input.axis.axis_max != binding->output.axis.axis_max) {
                        float normalized_value = (float)(value - binding->input.axis.axis_min) / (binding->input.axis.axis_max - binding->input.axis.axis_min);
                        value = binding->output.axis.axis_min + (int)(normalized_value * (binding->output.axis.axis_max - binding->output.axis.axis_min));
                    }
                } else {
                    value = 0;
                }
            } else if (binding->input_type == SDL_GAMEPAD_BINDTYPE_BUTTON) {
                if (SDL_GetJoystickButton(gamepad->joystick, binding->input.button)) {
                    value = binding->output.axis.axis_max;
                }
            } else if (binding->input_type == SDL_GAMEPAD_BINDTYPE_HAT) {
                int hat_mask = SDL_GetJoystickHat(gamepad->joystick, binding->input.hat.hat);
                if (hat_mask & binding->input.hat.hat_mask) {
                    value = binding->output.axis.axis_max;
                }
            }

            if (binding->output.axis.axis_min < binding->output.axis.axis_max) {
                valid_output_range = (value >= binding->output.axis.axis_min && value <= binding->output.axis.axis_max);
            } else {
                valid_output_range = (value >= binding->output.axis.axis_max && value <= binding->output.axis.axis_min);
            }
            /* SDL_GetGamepadAxis returns the first nonzero, valid output binding. */
            if (value != 0 && valid_output_range) {
                out[1 + output_axis] = value;
                axis_resolved[output_axis] = true;
            }
        }
    }

    out[0] = (int32_t) buttons;
    SDL_UnlockJoysticks();
    return true;
}

void VitaSDL_GetGamepadSnapshot(SDL_Gamepad *gamepad, int32_t *out)
{
    if (out == NULL) {
        SDL_InvalidParamError("out");
        return;
    }
    SDL_memset(out, 0, sizeof(int32_t) * 7);
    (void) SDL_VitaResolveGamepadSnapshot(gamepad, out);
}"""


def find_function_end(text: str, signature: str) -> int:
    if text.count(signature) != 1:
        raise SystemExit(f"expected exactly one SDL function signature: {signature}")
    start = text.index(signature)
    brace = text.find("{", start)
    if brace < 0:
        raise SystemExit(f"function body not found: {signature}")
    depth = 0
    state = "code"
    i = brace
    while i < len(text):
        c = text[i]
        n = text[i + 1] if i + 1 < len(text) else ""
        if state == "code":
            if c == "/" and n == "*":
                state = "block_comment"
                i += 1
            elif c == "/" and n == "/":
                state = "line_comment"
                i += 1
            elif c == '"':
                state = "string"
            elif c == "'":
                state = "char"
            elif c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    return i + 1
        elif state == "block_comment":
            if c == "*" and n == "/":
                state = "code"
                i += 1
        elif state == "line_comment":
            if c == "\n":
                state = "code"
        elif state in ("string", "char"):
            if c == "\\":
                i += 1
            elif (state == "string" and c == '"') or (state == "char" and c == "'"):
                state = "code"
        i += 1
    raise SystemExit(f"unterminated function body: {signature}")


def apply(text: str) -> str:
    if MARKER in text:
        return text
    end = find_function_end(text, SIGNATURE)
    return text[:end] + "\n\n" + HELPER + text[end:]


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    path = Path(sys.argv[1]) / "src/joystick/SDL_gamepad.c"
    if not path.is_file():
        raise SystemExit(f"SDL gamepad source not found: {path}")
    original = path.read_bytes()
    text = original.decode("utf-8")
    patched = apply(text)
    if patched != text:
        backup = Path(__file__).resolve().parents[4] / "scratchpad" / "sdl-gamepad-before-20261027" / "SDL_gamepad.c"
        backup.parent.mkdir(parents=True, exist_ok=True)
        if not backup.exists():
            backup.write_bytes(original)
        path.write_bytes(patched.encode("utf-8"))
        print("patched src/joystick/SDL_gamepad.c: one-pass Vita gamepad snapshot")
    else:
        print("SDL Vita gamepad snapshot: already present")


if __name__ == "__main__":
    main()
