# Recipe Guide

Recipe Guide is a PC-only Stacklands mod that makes the game's information panel persistent after a left click and lets players open craftable ingredients in cascading recipe panels.

## Features

- Click a card, Idea, quest, or Cardopedia entry to keep its information visible.
- A pin indicator appears in the top-right of the original information panel while it is locked.
- Click a craftable item name to open its recipe to the right.
- Only craftable entries that are available in the Ideas sidebar become expandable links; ordinary recipe ingredients stay plain text.
- Up to ten recipe levels, with repeated items in the current path disabled.
- Panels stack at the right edge when the screen is not wide enough.
- Opening and closing uses the game's DOTween transition library.
- Clicking another entry or an empty map area closes the existing recipe chain.

## 特色

- 点击卡牌、主意、任务或 Cardopedia 条目后，信息栏会保持显示。
- 信息栏锁定时，原版信息栏右上角会显示大头针标记。
- 点击有配方的物品名称，可以向右展开配方和原料。
- 只有主意侧边栏中可查看的有配方词条会显示为可展开链接，普通配方原料保持普通文字。
- 当前路径最多十层，已经出现过的物品不会重复展开。
- 屏幕宽度不足时，层级会在右侧自动堆叠。
- 展开和关闭使用游戏现有的 DOTween 动画过渡。
- 点击其他条目或地图空白区域，会关闭当前配方链。

## Build

Set the Stacklands path in `RecipeGuide.csproj.user`, then run:

```text
python build.py
```

The script builds the DLL and synchronizes it to the local Stacklands Mods directory.
