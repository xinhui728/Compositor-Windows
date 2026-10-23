#include "DitherPixels.h"
#include <stdio.h>
#include <string.h>

int main(void) {
    const int width = 23, height = 19;
    unsigned char original[23 * 19 * 4], pixels[23 * 19 * 4];
    static const unsigned char glyphs[] = {0,0,0,0, 0,255,255,0, 255,255,255,255};
    static const float coverage[] = {0, .5f, 1};
    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
        int i = (y * width + x) * 4;
        int a = x % 7 == 0 ? 0 : x % 3 == 0 ? 128 : 255;
        original[i] = ((x * 11 + y * 3) % 256) * a / 255;
        original[i+1] = ((x * 5 + y * 13) % 256) * a / 255;
        original[i+2] = ((x * 17 + y * 7) % 256) * a / 255;
        original[i+3] = a;
    }
    for (int style = 0; style <= 10; style++) {
        memcpy(pixels, original, sizeof pixels);
        DitherParams p = {0};
        p.style = style; p.levels = 3; p.diffusion = .8f; p.density = .13f; p.contrast = -.11f;
        p.cell = 8; p.angle = .25f; p.lightOnDark = 1; p.originalColors = style % 2;
        p.dark[0] = 10; p.dark[1] = 20; p.dark[2] = 80;
        p.light[0] = 230; p.light[1] = 160; p.light[2] = 60;
        p.glyphWidth = 2; p.glyphHeight = 2; p.glyphCount = 3; p.glyphs = glyphs; p.glyphCoverage = coverage;
        p.dots = .63f; p.wobble = 4;
        if (!dither_apply(pixels, width, height, width * 4, &p)) return 1;
        printf("%d:", style);
        for (unsigned int i = 0; i < sizeof pixels; i++) printf("%02x", pixels[i]);
        putchar('\n');
    }
    return 0;
}
