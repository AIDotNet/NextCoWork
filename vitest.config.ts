import { defineConfig } from 'vitest/config'

/**
 * 内核无头测试:不启动 Electron(方案 §13)。
 *
 * 刻意**不复用** electron.vite.config.ts —— 那份配置带着三个 environment
 * 和 external 规则,而这里要测的东西按设计就该在普通 Node 里跑得起来:
 * `src/shared/**` 是纯类型与纯函数,`src/main/kernel/**` 零 electron import。
 * 哪天某个测试因为「找不到 electron」挂了,那不是配置问题 ——
 * 是有人把 electron 依赖漏进内核了,这个测试就是报警器。
 */
export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
    // Windows CI 的真实 SQLite FULL 落盘、迁移和 TLS 初始化可超过默认的 5/10 秒。
    testTimeout: process.platform === 'win32' ? 30_000 : 5_000,
    hookTimeout: process.platform === 'win32' ? 30_000 : 10_000
  }
})
