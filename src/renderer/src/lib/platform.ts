/**
 * 平台判断 —— **只该用来处理窗口装饰上的平台差异**,别拿它做功能开关。
 *
 * 两件事需要它,而且是相反的两端:
 *
 * 1. **左上角。** macOS 的红绿灯占着窗口左上角,侧边栏表头和外层 Tab 条各为它硬留
 *    了一块位置(`pl-[74px]` / `pl-[78px]`,两个数都量自参考图)。Windows/Linux
 *    那边左上角是空的,这两块留白就成了纯空洞。
 *
 * 2. **右上角。** 反过来,Windows/Linux 在窗口右上角自绘最小化/最大化/关闭三颗按钮
 *    (`shell/WindowControls.tsx`),macOS 没有。所以那三颗按钮的渲染、以及两条顶栏
 *    给它让位的 `pr-window-controls`,同样由这个标志决定。
 *
 * ★ 第 2 条**以前不走这个标志**:那时用的是系统的 Window Controls Overlay,宽度靠
 *   `env(titlebar-area-*)` 问 overlay 自己要。overlay 已经被自绘取代(为什么,见
 *   `main/window/title-bar.ts` 的文件头),那个 CSS 变量也随之删了 —— 按钮既然是
 *   我们自己画的,宽度就是我们自己定的常数,没有什么可量的。
 *
 * ★ `typeof window` 那道判断是给 **vitest 的 node 环境**留的,不是防御式编程:
 *   手搓 JSDOM 的那些用例里,`vi.stubGlobal` 要等 `beforeEach` 才跑,而模块是在
 *   import 那一刻求值的 —— 少了它,任何一条会渲染到灯箱(它在这里读平台)的用例
 *   都会在 import 阶段抛 `window is not defined`。真实渲染进程里走不到那个分支
 *   (preload 没挂上时 `services/ipc.ts` 会先大声挂掉),那时结果也确实是「非 mac」
 *   这一档 —— 让错了边远好过整个文件加载不了。取值仍是 module-level:平台在一个
 *   进程的生命周期里不会变,见 `lib/accelerator.ts` 那条同源说明。
 */
export const IS_MAC =
  typeof window !== 'undefined' && window.nextcowork?.platform === 'darwin'
