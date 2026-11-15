# -*- coding: utf-8 -*-
"""Write the prompt files for the remaining plates. Run: python -I gen.py (from this folder)."""
import os

STYLE = (
    "A vintage colour-magazine illustration from the late 1950s, painted in gouache and reproduced by "
    "offset printing with visible fine halftone dots and very slight colour misregistration, on warm cream paper. "
    "Match the reference image exactly in painting style, print texture, palette, the same tabby cat, the same "
    "brass-and-walnut calculating machine and the same wooden desk.\n\n"
)
TAIL = (
    "\n\nPalette drawn from old print: warm cream paper (#F8F4ED), deep espresso brown (#2A2019), camel and ochre "
    "(#9C7A54), muted brass, one restrained accent of dusty teal. Soft, matte, poetic and restrained. "
    "No neon, no glow, no modern objects, no screens.\n\n"
    "No text, no letters, no legible numbers, no logo, no border, no frame, no watermark."
)

PLATES = {
    "night": (
        "Scene: the same desk at night, in a dark room. The calculating machine sits with its walnut casing fully "
        "closed and latched, a sealed plain box; nothing inside can be seen. The lamp is switched off. A single "
        "finished strip of paper lies in front of the box. The tabby cat sits a little apart in the shadows, "
        "looking at the closed box, only its outline and eyes catching a faint light from a window off-frame.\n\n"
        "Composition: wide landscape, very dark overall: deep espresso-black (#15120E) fills most of the frame. "
        "The box and cat sit low in the right third. The left two thirds are almost empty darkness, quiet space "
        "for large light-coloured text. Low contrast, still, a little mysterious but not frightening."
    ),
    "check": (
        "Scene: close view of the desk top. A hand holds a brass magnifying glass over a long strip of printed "
        "paper tape, studying one row closely. The tabby cat's paw rests on the far end of the tape, holding it "
        "flat. A fountain pen lies beside it.\n\n"
        "Composition: square, viewed from slightly above. Subject centred with the cream paper background "
        "fading out softly at all four edges like a magazine spot illustration with no hard border."
    ),
    "duo": (
        "Scene: two calculating machines of the same family but clearly different makes (one walnut and brass, "
        "one dark green enamel and nickel) stand side by side on the desk, both with casings open, each feeding "
        "out its own paper tape. The tabby cat sits between them holding the two tapes side by side under one "
        "paw, comparing them, head tilted.\n\n"
        "Composition: wide landscape, symmetrical, subject across the lower two thirds. Upper third is calm "
        "cream paper. Edges fade softly into the paper like a magazine spot illustration with no hard border."
    ),
    "press": (
        "Scene: on the desk, a neat stack of finished documents: a bound report, a folder of charts with a "
        "bar chart and a pie chart visible as simple shapes, a deck of presentation cards fanned out, a roll of "
        "film, and a small framed picture. The calculating machine is behind them, its paper tape feeding into "
        "the stack. The tabby cat sits on top of the bound report, satisfied.\n\n"
        "Composition: square. Subject centred, cream paper background fading out softly at all four edges like "
        "a magazine spot illustration with no hard border."
    ),
    "ledger": (
        "Scene: an open account ledger with ruled columns lies on the desk, a fountain pen resting in its "
        "gutter, a short stack of coins and a small brass coin scale beside it. The paper tape from the machine "
        "runs straight across the open ledger page. The tabby cat lies next to the ledger with one paw on the "
        "page, watching the coins.\n\n"
        "Composition: wide landscape, seen from slightly above. The ledger and cat fill the left two thirds; "
        "the right third fades to calm cream paper, quiet space for text. Edges fade softly into the paper."
    ),
    "close": (
        "Scene: late evening, the lamp lit warm. The calculating machine stands open and quiet, its work "
        "finished, the long paper tape neatly coiled beside it. The tabby cat is curled up asleep on top of the "
        "closed account ledger, in the pool of lamp light.\n\n"
        "Composition: wide landscape. Machine, lamp and sleeping cat sit in the left third. The right two "
        "thirds are calm, empty warm cream wall fading to paper, quiet space for text."
    ),
}

here = os.path.dirname(os.path.abspath(__file__))
for name, scene in PLATES.items():
    with open(os.path.join(here, name + ".txt"), "w", encoding="utf-8") as f:
        f.write(STYLE + scene + TAIL)
print("wrote", len(PLATES))
