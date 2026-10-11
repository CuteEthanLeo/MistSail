# -*- coding: utf-8 -*-
"""把配乐与配音混合：配音期间配乐自动压低（ducking），输出 final_audio.wav。"""
import json, os
import numpy as np
from scipy.io import wavfile
from scipy.ndimage import uniform_filter1d

HERE = os.path.dirname(os.path.abspath(__file__))
SR = 44100
tl = json.load(open(os.path.join(HERE, "timeline.json"), encoding="utf-8"))
N = int(tl["duration"] * SR)

sr, music = wavfile.read(os.path.join(HERE, "music.wav"))
assert sr == SR
music = music.astype(np.float32) / 32767
if len(music) < N:
    music = np.vstack([music, np.zeros((N - len(music), 2), np.float32)])
music = music[:N]

voice = np.zeros(N, np.float32)
duck = np.ones(N, np.float32)
for sub in tl["subs"]:
    sr2, a = wavfile.read(os.path.join(HERE, "tts", sub["clip"]))
    a = a.astype(np.float32) / 32767
    i = int(sub["start"] * SR)
    a = a[: max(0, N - i)]
    voice[i:i + len(a)] += a
    duck[max(0, i - int(0.25 * SR)): i + len(a) + int(0.15 * SR)] = 0.3
duck = uniform_filter1d(duck, int(0.35 * SR))  # 平滑过渡

# 配音响度统一到约 -16 dBFS RMS（只统计有声部分）
act = np.abs(voice) > 0.01
vrms = np.sqrt(np.mean(voice[act] ** 2)) if act.any() else 1.0
voice *= (10 ** (-16 / 20)) / vrms
voice = np.tanh(voice * 1.2) / 1.2

music_gain = 10 ** (-3 / 20)  # 配乐整体略低于之前的纯音乐版
out = music * (duck * music_gain)[:, None] + voice[:, None]
peak = np.abs(out).max()
if peak > 0.97:
    out *= 0.97 / peak
wavfile.write(os.path.join(HERE, "final_audio.wav"), SR, (out * 32767).astype(np.int16))
print("audio mixed", N / SR, "s, peak", np.abs(out).max())
