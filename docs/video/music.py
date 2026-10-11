# -*- coding: utf-8 -*-
"""离线合成一段 63 秒 lo-fi 风格背景音乐（numpy + scipy），输出 music.wav。"""
import os
import numpy as np
from scipy.io import wavfile
from scipy.signal import fftconvolve, butter, sosfilt

SR = 44100
import json as _j
DUR = float(_j.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "timeline.json"), encoding="utf-8"))["duration"]) if os.path.exists(os.path.join(os.path.dirname(os.path.abspath(__file__)), "timeline.json")) else 63.0
N = int(SR * DUR)
BPM = 92
BEAT = 60.0 / BPM
BAR = BEAT * 4
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "music.wav")
rng = np.random.RandomState(42)


def midi(n):
    return 440.0 * 2 ** ((n - 69) / 12)


def add(buf, start, sig):
    i = int(start * SR)
    if i >= N or i < 0:
        return
    sig = sig[: N - i]
    buf[i:i + len(sig)] += sig


def env_adsr(n, a, d, s, r, sr=SR):
    t = np.arange(n) / sr
    total = n / sr
    e = np.ones(n)
    a_n = int(a * sr)
    d_n = int(d * sr)
    r_n = int(r * sr)
    if a_n > 0:
        e[:a_n] = np.linspace(0, 1, a_n)
    if d_n > 0:
        e[a_n:a_n + d_n] = np.linspace(1, s, d_n)[: max(0, n - a_n)]
    e[a_n + d_n:] = s
    if r_n > 0 and r_n < n:
        e[-r_n:] *= np.linspace(1, 0, r_n)
    return e


def lowpass(sig, fc, order=2):
    sos = butter(order, fc / (SR / 2), btype="low", output="sos")
    return sosfilt(sos, sig)


def highpass(sig, fc, order=2):
    sos = butter(order, fc / (SR / 2), btype="high", output="sos")
    return sosfilt(sos, sig)


def bandpass(sig, lo, hi, order=2):
    sos = butter(order, [lo / (SR / 2), hi / (SR / 2)], btype="band", output="sos")
    return sosfilt(sos, sig)


# 和弦进行（MIDI）：Am7  Fmaj7  Cmaj7  G6 ，每小节一个
CHORDS = [
    [57, 60, 64, 67],   # A C E G
    [53, 57, 60, 64],   # F A C E
    [48, 52, 55, 59],   # C E G B
    [55, 59, 62, 64],   # G B D E
]
N_BARS = int(np.ceil(DUR / BAR)) + 1


def pad_note(freq, dur):
    n = int(dur * SR)
    t = np.arange(n) / SR
    sig = np.zeros(n)
    for det in (-0.004, 0.0, 0.004):
        f = freq * (1 + det)
        for h in range(1, 6):
            sig += np.sin(2 * np.pi * f * h * t + rng.uniform(0, 6.28)) / (h ** 1.6)
    sig *= env_adsr(n, 0.6, 0.4, 0.8, 0.9)
    # 缓慢的滤波摆动
    lfo = 0.5 + 0.5 * np.sin(2 * np.pi * 0.15 * t + rng.uniform(0, 6.28))
    sig = lowpass(sig, 900 + 700 * lfo.mean())
    return sig / 3.0


def pluck(freq, dur=0.9, bright=1.0):
    n = int(dur * SR)
    t = np.arange(n) / SR
    sig = np.sin(2 * np.pi * freq * t) + 0.45 * bright * np.sin(2 * np.pi * freq * 2 * t + 0.3) + 0.18 * bright * np.sin(2 * np.pi * freq * 3 * t + 0.7)
    sig *= np.exp(-t * 4.5) * (1 - np.exp(-t * 600))
    return sig


def bass_note(freq, dur):
    n = int(dur * SR)
    t = np.arange(n) / SR
    sig = np.sin(2 * np.pi * freq * t) + 0.25 * np.sin(2 * np.pi * freq * 2 * t)
    sig *= env_adsr(n, 0.01, 0.1, 0.8, 0.12)
    return lowpass(sig, 220)


def kick():
    n = int(0.35 * SR)
    t = np.arange(n) / SR
    f = 42 + 110 * np.exp(-t * 28)
    ph = 2 * np.pi * np.cumsum(f) / SR
    sig = np.sin(ph) * np.exp(-t * 9)
    sig += 0.3 * rng.randn(n) * np.exp(-t * 180)
    return sig


def snare():
    n = int(0.25 * SR)
    t = np.arange(n) / SR
    noise = bandpass(rng.randn(n), 900, 5000) * np.exp(-t * 18)
    body = np.sin(2 * np.pi * 175 * t) * np.exp(-t * 30)
    return 0.7 * noise + 0.5 * body


def hat(open_=False):
    n = int((0.22 if open_ else 0.07) * SR)
    t = np.arange(n) / SR
    return highpass(rng.randn(n), 7000) * np.exp(-t * (12 if open_ else 55))


def whoosh(dur=0.9):
    n = int(dur * SR)
    t = np.arange(n) / SR
    sig = rng.randn(n)
    env = np.sin(np.pi * np.clip(t / dur, 0, 1)) ** 2
    sig = bandpass(sig, 300, 4000) * env
    return sig


pad = np.zeros(N)
plk = np.zeros(N)
bass = np.zeros(N)
drums = np.zeros(N)
fx = np.zeros(N)

for bar in range(N_BARS):
    t0 = bar * BAR
    chord = CHORDS[bar % 4]
    # pad
    for note in chord:
        add(pad, t0, pad_note(midi(note), BAR + 0.5))
    # 琶音：8 分音符，顺序 1-3-2-4-1-3-2-4 ，隔小节升八度
    order = [0, 2, 1, 3, 0, 2, 1, 3]
    for k, idx in enumerate(order):
        if bar < 2 and k % 2 == 1:
            continue  # 开头稀疏些
        octv = 12 if (bar % 2 == 1 and k >= 4) else 0
        vel = 0.9 if k % 2 == 0 else 0.6
        # 轻微 swing
        tt = t0 + k * BEAT / 2 + (0.03 if k % 2 else 0.0)
        add(plk, tt, vel * pluck(midi(chord[idx] + 12 + octv)))
    # 贝斯
    root = chord[0] - 12
    add(bass, t0, bass_note(midi(root), BEAT * 1.6))
    add(bass, t0 + BEAT * 2.5, bass_note(midi(root), BEAT * 0.7))
    add(bass, t0 + BEAT * 3.5, bass_note(midi(root + 7 if bar % 4 != 3 else root + 5), BEAT * 0.5))
    # 鼓：从第 3 小节开始；第 9 小节起加踩镲
    if bar >= 2 and t0 < DUR - 3.5:
        add(drums, t0, 1.0 * kick())
        add(drums, t0 + BEAT * 2, 0.9 * kick())
        if bar % 4 == 3:
            add(drums, t0 + BEAT * 3.5, 0.6 * kick())
        add(drums, t0 + BEAT, 0.55 * snare())
        add(drums, t0 + BEAT * 3, 0.6 * snare())
        if bar >= 8:
            for k in range(8):
                add(drums, t0 + k * BEAT / 2 + (0.02 if k % 2 else 0), (0.28 if k % 2 == 0 else 0.16) * hat(open_=(k == 7)))

# 场景切换的 whoosh
for st in ([sc[0] for sc in _j.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "timeline.json"), encoding="utf-8"))["scenes"][1:]] if os.path.exists(os.path.join(os.path.dirname(os.path.abspath(__file__)), "timeline.json")) else [6.0, 16.0, 24.0, 38.0, 50.0, 57.0]):
    add(fx, st - 0.55, 0.35 * whoosh())

# 混响（只给 pad / 琶音）
ir_n = int(1.6 * SR)
ir = rng.randn(ir_n) * np.exp(-np.arange(ir_n) / SR / 0.45)
ir = lowpass(ir, 5000)
ir /= np.abs(ir).sum() ** 0.5 * 4
wet = fftconvolve(pad * 0.9 + plk * 0.7, ir)[:N]

mix = 0.55 * pad + 0.55 * plk + 0.7 * bass + 0.5 * drums + 0.55 * wet + fx
mix = lowpass(mix, 12000)
# 淡入淡出
fade_in = int(0.8 * SR)
mix[:fade_in] *= np.linspace(0, 1, fade_in)
fade_out = int(3.5 * SR)
mix[-fade_out:] *= np.linspace(1, 0, fade_out) ** 1.5
# 软限幅 + 归一化
mix = np.tanh(mix / (np.abs(mix).max() * 0.6)) * 0.6
rms = np.sqrt(np.mean(mix ** 2))
target = 10 ** (-19 / 20)
mix *= min(target / rms, 0.95 / np.abs(mix).max())
stereo = np.stack([mix, mix], axis=1)
# 轻微立体声宽度：右声道延迟 8ms 的混响
delay = int(0.008 * SR)
side = np.zeros(N)
side[delay:] = wet[:-delay] * 0.2
stereo[:, 0] += side * 0.5
stereo[:, 1] -= side * 0.5
stereo = np.clip(stereo, -0.98, 0.98)
wavfile.write(OUT, SR, (stereo * 32767).astype(np.int16))
print("music written", OUT, "peak", np.abs(stereo).max(), "rms", rms)
