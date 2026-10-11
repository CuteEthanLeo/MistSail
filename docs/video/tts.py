# -*- coding: utf-8 -*-
"""用 edge-tts 为每条字幕生成配音，并按朗读时长重排字幕/场景时间轴，写出 timeline.json。"""
import asyncio, json, os, subprocess, sys
import numpy as np
from scipy.io import wavfile
import edge_tts
import imageio_ffmpeg

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import render as R  # noqa: E402

FF = imageio_ffmpeg.get_ffmpeg_exe()
VOICE = "zh-CN-YunxiNeural"
RATE = sys.argv[1] if len(sys.argv) > 1 else "+12%"
TTS_DIR = os.path.join(HERE, "tts")
os.makedirs(TTS_DIR, exist_ok=True)

# 朗读文本可以和屏幕字幕略有不同（去掉符号、读起来更顺）
SUBS = list(R.SUBS) + [(R.DURATION - 0.5, R.DURATION, "本片由 Claude Fable 5.1 制作")]
SPOKEN = {
    "雾帆 MistSail：一款基于 WPF / .NET 8 的开源 SNI 处理工具": "雾帆 MistSail，一款基于 WPF 和 .NET 8 的开源 SNI 处理工具。",
    "HTTPS 加密了网页内容，却有一处例外：握手阶段的 SNI": "HTTPS 加密了网页内容，却有一处例外，握手阶段的 SNI。",
    "SNI 以明文写着你要访问的域名，链路上任何设备都能看见": "SNI 以明文写着你要访问的域名，链路上任何设备都能看见。",
    "于是中间设备可以据此嗅探、劫持，甚至直接重置你的连接": "于是中间设备可以据此嗅探、劫持，甚至直接重置你的连接。",
    "内容是加密的，但「你在访问谁」却是公开的": "内容是加密的，但你在访问谁，却是公开的。",
    "雾帆的思路：不加代理，在本机接管握手": "雾帆的思路，不加代理，在本机接管握手。",
    "hosts 把域名指向本机，由本机 nginx 改写 SNI 后连向真实服务器": "hosts 把域名指向本机，由本机 nginx 改写 SNI 后，连向真实服务器。",
    "Cloudflare 类站点则由回环中继把 ClientHello 切片透传": "Cloudflare 类站点，则由回环中继把 ClientHello 切片透传。",
    "核心代码只有一百多行：定位 SNI 扩展，按字节切分，重排记录边界": "核心代码只有一百多行，定位 SNI 扩展，按字节切分，重排记录边界。",
    "根证书本机自签，只装当前用户、只信任本机 nginx；分片路线不解密": "根证书本机自签，只装当前用户，只信任本机 nginx。分片路线不解密。",
    "所有参数都在一份 JSON 里，分片字节、间隔、模式随时可调": "所有参数都在一份 JSON 里，分片字节、间隔、模式，随时可调。",
    "主界面只有一个电源按钮，连通性检测与诊断信息一键复制": "主界面只有一个电源按钮，连通性检测与诊断信息，一键复制。",
    "无代理服务器，不改路由，流量不经任何第三方": "无代理服务器，不改路由，流量不经任何第三方。",
    "开源可审计 · 仅供抵御网络非法监听与网络安全研究 · 请遵守当地法律法规": "开源可审计。仅供抵御网络非法监听与网络安全研究，请遵守当地法律法规。",
    "本片由 Claude Fable 5.1 制作": "本片由 Claude Fable 5.1 制作。",
}


async def synth(i, text):
    mp3 = os.path.join(TTS_DIR, f"{i:02d}.mp3")
    wav = os.path.join(TTS_DIR, f"{i:02d}.wav")
    if not os.path.exists(mp3):
        await edge_tts.Communicate(text, VOICE, rate=RATE, proxy="http://127.0.0.1:7897").save(mp3)
    subprocess.check_call([FF, "-y", "-loglevel", "error", "-i", mp3, "-ac", "1", "-ar", "44100", wav])
    sr, a = wavfile.read(wav)
    a = a.astype(np.float32) / 32768
    # 去掉首尾静音
    nz = np.where(np.abs(a) > 0.01)[0]
    s, e = (nz[0], nz[-1]) if len(nz) else (0, len(a))
    trimmed = a[max(0, s - int(0.05 * sr)): e + int(0.12 * sr)]
    wavfile.write(wav, sr, (trimmed * 32767).astype(np.int16))
    return len(trimmed) / sr


async def main():
    durs = []
    for i, (_, _, text) in enumerate(SUBS):
        d = await synth(i, SPOKEN.get(text, text))
        durs.append(d)
        print(f"{i:02d} {d:5.2f}s  {text}")
    # 重排：按场景分组
    scenes = []
    subs = []
    cur = 0.0
    k = 0
    for si, (s, e, _) in enumerate(R.SCENES):
        group = [j for j, (ss, _, _) in enumerate(SUBS) if s <= ss < e or (si == len(R.SCENES) - 1 and ss >= e)]
        new_s = cur
        prev_end = new_s
        for n, j in enumerate(group):
            ss, _, text = SUBS[j]
            offset = max(0.0, ss - s)
            start = max(new_s + offset, prev_end + 0.25) if n else new_s + offset
            end = start + durs[j] + 0.05
            subs.append({"start": round(start, 3), "end": round(end, 3), "text": text, "clip": f"{j:02d}.wav"})
            prev_end = end
        new_e = max(new_s + (e - s), prev_end + (1.6 if si == len(R.SCENES) - 1 else 0.4))
        scenes.append([round(new_s, 3), round(new_e, 3)])
        cur = new_e
    timeline = {"duration": round(cur, 3), "scenes": scenes, "subs": subs, "voice": VOICE, "rate": RATE}
    with open(os.path.join(HERE, "timeline.json"), "w", encoding="utf-8") as fh:
        json.dump(timeline, fh, ensure_ascii=False, indent=1)
    print("total duration", cur)
    for sc in scenes:
        print(" scene", sc)


asyncio.run(main())
