import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import './styles/theme.css'
import './styles/arc-integration.css'
import { I18nProvider } from './i18n'
import { MigrationGateHost } from './views/MigrationGate'
import { IS_MAC } from './lib/platform'

// 苹果风格:macOS 窗口开了 vibrancy(见 main/index.ts),界面据此把窗口底和主面板改成半透明玻璃
if (IS_MAC) document.documentElement.dataset.vibrancy = 'true'

const container = document.getElementById('root')
if (!container) throw new Error('#root not found')

createRoot(container).render(
  <StrictMode>
    <I18nProvider>
      {/*
        ★ 闸门在 App **外面**,不是叠在它上面。迁移期间数据库还没打开,App 的
        握手 effect 一跑就会 invoke `app:getBootstrap` 并失败,把首屏置成
        「握手失败」—— 而闸门一放行,用户看到的就是那一屏错误。
        包在外面,App 的 effect 在闸门结束之前一次都不会跑(见 MigrationGate 文件头)。

        ★ 放行还要多等一条:主进程得把 handler 装完(`MigrationState.ipcReady`)。
        数据库没打开只是其中一种情况 —— 建窗在 `registerIpc()` 之前,所以**没有
        迁移的那次启动**同样会撞上「No handler registered for 'app:getBootstrap'」。
        判据在 `views/migration-release.ts`。
      */}
      <MigrationGateHost>
        <App />
      </MigrationGateHost>
    </I18nProvider>
  </StrictMode>
)
