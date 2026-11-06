import js from '@eslint/js'
import tseslint from 'typescript-eslint'

export default tseslint.config(
  /*
    `dist/**` 只挡得住仓库根那一个。示例插件的构建产物在
    `examples/<id>/dist/` 下 —— 那里面是 esbuild 打出来的 8MB bundle,
    让 lint 去读它既没有意义,又会因为「浏览器全局在 Node 配置里没定义」
    刷出几十条假报错。

    `resources/plugin-runtime/**` 同理:那是 `scripts/build-plugin-runtime.mjs`
    产出的 React 与控件包(压缩过、不进版本库),和 `out/` 是同一类东西。
    不挡的话 `npm run lint` 会因为它多出一千多条 `'document' is not defined`——
    而那些「报错」指的是一份本来就只在浏览器里跑的产物。
  */
  // `plugins/*/dist/` 与 examples 的 dist 同理:第一方插件(办公编辑器)的构建产物
  { ignores: ['out/**', 'dist/**', '.tscache/device-components/**', 'examples/*/dist/**', 'plugins/*/dist/**', 'node_modules/**', 'packages/*/template/**', 'resources/plugin-runtime/**'] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    rules: {
      '@typescript-eslint/no-unused-vars': ['warn', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
      '@typescript-eslint/no-explicit-any': 'warn'
    }
  },
  {
    /**
     * `resources/*.cjs` 是**原样拷贝进安装包**的手写 CommonJS 资源,
     * 目前只有插件宿主窗口的 preload(见那个文件的头:它是安全边界,
     * 应该能被逐行读完,所以不打包)。它跑在 Electron 的沙箱 preload 环境里,
     * `require('electron')` 是那个环境**唯一**允许的 require —— 不是遗留写法。
     */
    files: ['resources/**/*.cjs'],
    languageOptions: {
      globals: { require: 'readonly', module: 'readonly', console: 'readonly' }
    },
    rules: {
      '@typescript-eslint/no-require-imports': 'off'
    }
  },
  {
    /**
     * `scripts/` 下是跑在**宿主 Node** 里的一次性工具(端到端冒烟等),不在任何
     * tsconfig 的 include 里,所以 `no-undef` 这条规则真的会生效 ——
     * src 下的 .ts 由 typescript-eslint 关掉了它,那边靠 tsc 查符号。
     *
     * 不引 `globals` 包:这里要的就这几个,列出来比多一个依赖清楚。
     */
    files: ['scripts/**/*.mjs', 'packages/**/*.mjs', 'examples/**/*.mjs', 'plugins/**/*.mjs', '*.config.js', '*.config.mjs'],
    languageOptions: {
      globals: {
        process: 'readonly',
        console: 'readonly',
        fetch: 'readonly',
        WebSocket: 'readonly',
        JSON: 'readonly',
        URL: 'readonly',
        Buffer: 'readonly',
        FormData: 'readonly',
        Blob: 'readonly'
      }
    }
  },
  {
    /**
     * `packages/plugin-api/*.d.ts` 是**发布给插件作者**的环境声明包。
     *
     * ★ 这里必须用三斜线 `path` 引用:`nextcowork.d.ts` 要把
     * `nextcowork-view.d.ts`(`nextcowork/ui` 与 `nextcowork/view`)一并带进来,
     * 而一旦改成 `import`,这个文件就变成了**模块** —— 于是里面的
     * `declare module 'nextcowork'` 从「声明一个模块」变成「增强一个已存在的模块」,
     * 而 `nextcowork` 在作者那边并不真的存在(运行期才由宿主注入)。
     * 结果是作者的 `import * as ncw from 'nextcowork'` 直接报「找不到模块」。
     *
     * 分两个文件而不是合成一个,是因为它们描述的是**两个不同的执行环境**:
     * 插件宿主页(有 preload、有权限链)和视图 iframe(只有一条 postMessage 通道)。
     */
    files: ['packages/plugin-api/*.d.ts'],
    rules: {
      '@typescript-eslint/triple-slash-reference': 'off'
    }
  },
  {
    /**
     * 各个包下面的 `template` 目录是**生成给别人的**源码,不是本仓库的源码:
     * 它 import 的 `nextcowork` 在这里根本不存在(那是宿主在运行期注入的),
     * 而里面的 `__PUBLISHER__` 之类占位符也不是合法标识符。
     * 让它进 lint 只会产生一堆必须被忽略的报错。
     *
     * `template-view` 是 `--view` 叠加上去的那一份,理由完全相同 ——
     * 它 import 的 `react` / `nextcowork/ui` 也都是宿主运行期才注入的裸模块名。
     */
    ignores: ['packages/*/template/**', 'packages/*/template-view/**']
  }
)
