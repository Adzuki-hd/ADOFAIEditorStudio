#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""UserPromptSubmit hook：按关键词把「这条消息该用哪个技能」注进上下文。

为什么需要它：用户不熟技能名、也不打算记；而我干活时会惯性用手里已经握着的
工具（Bash / Read / Grep），那一长串技能清单不会自己触发 —— 今天整理 Obsidian
笔记库时就是先用 bash 翻了半天，用户提醒才切到 obsidian-cli。
写成 hook 比写在记忆里更硬。

设计要点（三条都不能破）：
  · **不匹配就什么都不输出** —— 保持安静，不给每一轮都塞噪音。
  · **任何异常都静默退出（exit 0）** —— 这个 hook 绝不能打断对话。
  · 输出走 JSON 的 hookSpecificOutput.additionalContext，并
    suppressOutput 把 stdout 从对话里藏掉，用户看不见这行。
"""

import json
import re
import sys

# (正则, 要提醒的技能)。一条命中提醒一条，去重后拼起来。
# 正则对**用户原话**匹配，所以要照用户会怎么说来写，不是照技能名写。
RULES = [
    (r"笔记|obsidian|知识库",
     "obsidian-cli（动笔记库用它，别用 bash 翻文件）"),
    (r"pacl2|反编译|版本绕过|打包|重打包|反汇编",
     "adofai-modding（ADOFAI 内部 / PACL2 / 反编译 / 打包 / 版本绕过）"),
    (r"联机|做谱同步|房主|内网穿透|多人",
     "adofai-mp-chart（联机做谱协议）"),
    (r"设计|间距|层次|对齐|配色|视觉|ui\s*审查",
     "design-doctrine 或 design-review（UI / 设计审查）"),
    (r"bug|报错|异常|崩|根因|排查|为什么.{0,6}(不对|没有|不|失败)",
     "investigate（先取证再改，别猜着修）"),
    (r"第二意见|复核|再检查|独立.{0,4}(看|判断)",
     "codex（拉一个独立意见）"),
    (r"发版|发布|变更日志|更新日志|changelog",
     "ship 或 changelog-generator"),
    (r"软著|著作权",
     "softcopyright"),
]


def main():
    # 先把 stdin 读干净，解析失败就退出 —— UserPromptSubmit 给的是 JSON。
    try:
        # **按字节读再自己按 UTF-8 解。** 中文 Windows 上 sys.stdin 的编码
        # 是 cp936，直接 read() 会把 UTF-8 的中文解坏，正则就再也匹配不上。
        raw = sys.stdin.buffer.read().decode("utf-8", "replace")
        if not raw.strip():
            return
        prompt = json.loads(raw).get("prompt") or ""
    except Exception:
        return
    if not prompt:
        return

    hits = []
    for pattern, skill in RULES:
        try:
            if skill not in hits and re.search(pattern, prompt, re.IGNORECASE):
                hits.append(skill)
        except Exception:
            continue
    if not hits:
        return

    out = {
        "suppressOutput": True,
        "hookSpecificOutput": {
            "hookEventName": "UserPromptSubmit",
            "additionalContext": "（技能路由）这条消息可能对得上：" + "；".join(hits)
                                 + "。对得上就直接用，不用等用户点名。",
        },
    }
    # ensure_ascii 用默认的 True：Windows 下 stdout 未必是 UTF-8，
    # 直写中文可能抛 UnicodeEncodeError；转义成 \\uXXXX 由对面解析回中文，稳妥。
    sys.stdout.write(json.dumps(out))


if __name__ == "__main__":
    main()
