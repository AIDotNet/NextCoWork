using System.Security.Cryptography;

namespace Ncw.Client.Widgets;

/// <summary>
/// WebView 里那一页「外壳」的装配(纯函数,字符串进字符串出,单测可测;WebView 只负责加载它)。
/// <para><b>安全模型</b>(docs/design/03 §8):一个 widget = 一个独立的 WebView 页面(非持久数据存储、没有任何控制端状态、没有任何本机能力),
/// 页面里跑的是模型写的代码,所以<b>页面发出的一切都当成不可信输入</b>(<see cref="WidgetInboundParser"/>)。四道防线:
/// 服务端消毒(<c>Ncw.Agent.Widgets</c>)→ 外壳的 CSP(<see cref="Csp"/>,默认拒绝一切网络,脚本只认本页的 nonce 和 CDN 白名单)→ 导航与弹窗全部拦下
/// (<see cref="WidgetNavigationPolicy"/>)→ 桥消息白名单 + 限速(<see cref="WidgetBridge"/>)。</para>
/// </summary>
public static class WidgetShell
{
    /// <summary>
    /// 允许 <c>&lt;script src&gt;</c> / <c>&lt;link rel=stylesheet&gt;</c> 取回资源的 CDN(精确主机名)。<b>必须与 Agent 的 <c>WidgetUrls.CdnHosts</c> 一致、不得更宽</b>
    /// (<c>WidgetShellTests</c> 在能同时引用两边的 <c>Ncw.UI.Tests</c> 里逐项核对)。设计规范里 Chart.js / D3 / mermaid 的例子引用的就是它们。
    /// </summary>
    public static IReadOnlyList<string> CdnHosts { get; } = ["cdn.jsdelivr.net", "unpkg.com", "cdnjs.cloudflare.com", "esm.sh"];

    /// <summary>一次性 nonce(每个页面一个,CSPRNG)。固定值等于换个写法的 <c>unsafe-inline</c>。</summary>
    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// 页面的 CSP(写在 <c>&lt;meta http-equiv&gt;</c> 里:<c>NavigateToString</c> 没有响应头可用)。逐条理由:
    /// <list type="bullet">
    /// <item><c>default-src 'none'</c>:什么都不给,下面逐项开。</item>
    /// <item><c>script-src 'nonce-…' &lt;CDN&gt;</c>:只有外壳自己的脚本和宿主逐个贴上 nonce 的 widget 内联脚本、以及白名单 CDN 的库能跑。<b>没有 <c>unsafe-inline</c> / <c>unsafe-eval</c></b>:
    /// 内联事件处理属性、<c>eval</c>、<c>new Function</c> 都被拦(真实 WebView 上实测:WKWebView)。</item>
    /// <item><c>style-src 'unsafe-inline' &lt;CDN&gt;</c>:规范要求内联 style。样式不是代码。</item>
    /// <item><c>img-src data: blob:</c>:<b>不放任何外链图片</b>——服务端消毒本来就拿掉外链图片(会泄露「谁在看」),这里不比服务端更宽(2.x 的 CSP 放了 <c>https:</c>)。</item>
    /// <item><c>connect-src 'none'</c>:<b>程序化出网默认全拒</b>(2.x 放了 CDN;3.0 更严:<c>fetch</c> / <c>XMLHttpRequest</c> / WebSocket / EventSource 一律不行)。</item>
    /// <item><c>frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'</c>:没有嵌套页面、插件、改基址、表单提交。</item>
    /// </list>
    /// </summary>
    public static string Csp(string nonce)
    {
        var cdn = string.Join(' ', CdnHosts.Select(h => "https://" + h));
        return string.Join("; ",
            "default-src 'none'",
            $"script-src 'nonce-{nonce}' {cdn}",
            $"style-src 'unsafe-inline' {cdn}",
            "img-src data: blob:",
            $"font-src data: {cdn}",
            "media-src data: blob:",
            "connect-src 'none'",
            "frame-src 'none'",
            "object-src 'none'",
            "base-uri 'none'",
            "form-action 'none'");
    }

    /// <summary>外壳页面(不含任何 widget 内容:内容在页面就绪后经 <see cref="WidgetPush.Content"/> 推进去)。</summary>
    public static string Build(string nonce) =>
        $$"""
        <!doctype html>
        <html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="{{Csp(nonce)}}">
        <meta name="ncw-widget-nonce" content="{{nonce}}">
        <style>
        {{WidgetBaseStyles.Css}}
        </style>
        <style>
        {{ShellStyles}}
        </style>
        </head><body>
        <div id="ncw-loading"></div>
        <div id="ncw-root"></div>
        <script nonce="{{nonce}}">
        {{Runtime}}
        </script>
        </body></html>
        """;

    /// <summary>外壳自己的样式:背景透明、无外边距(widget 要长在对话里,不是嵌了一张白纸),基础样式表在它前面,所以这里的规则盖得住它。</summary>
    internal const string ShellStyles = """
        * { box-sizing: border-box; }
        [hidden] { display: none !important; }
        html, body { margin: 0; padding: 0; background: transparent; }
        /* 页面自己不滚:宿主只把 WebView 摆在卡片的可见段上,用 ncw:widget:viewport 告诉页面滚到哪(脚本滚动不受 overflow:hidden 影响);
           没被页面里的元素消费的滚轮由外壳脚本交给宿主(ncw:widget:wheel)去滚转录 */
        html, body { overflow: hidden; }
        body {
          font-family: var(--font-sans);
          font-size: 14px;
          color: var(--color-text-primary);
          -webkit-font-smoothing: antialiased;
        }
        #ncw-root { display: flow-root; padding: 2px; }
        #ncw-loading { color: var(--color-text-secondary); font-size: 13px; padding: 2px; }
        """;

    /// <summary>
    /// 页面侧运行时(外壳的脚本,<b>带本页 nonce 的唯一一段内联脚本</b>)。它做这几件事:① 收宿主消息(<c>__ncwHost.recv</c>);② 把内容塞进 <c>#ncw-root</c>、
    /// 逐个重建脚本并带上 nonce(按顺序:有 <c>src</c> 的等它加载完再跑下一个,Chart.js 之类的库才在内联脚本之前就绪);③ 量高度上报(量 <c>#ncw-root</c> 而不是 body,
    /// 否则会把宿主刚设的高度量回去、无限长高);④ 提供 <c>window.ncw</c>;⑤ 把链接点击转成 <c>openUrl</c> 调用(导航本身由宿主拦下);⑥ 按宿主给的可见段起点滚动自己(<c>viewport</c>);
    /// ⑦ 把页面没有消费的滚轮交给宿主(<c>wheel</c>),在小组件上滚动也能滚动转录。
    /// <para>没有 <c>sendPrompt()</c>:它是 widget 通往 Agent 主循环的输入通道,2.x 同样不给。</para>
    /// <para>手写的 JS 常量(不引入 Node / TypeScript 构建,AGENTS.md §6);没有 JS 引擎可以在单测里跑它,所以它的行为只在真实 WebView 里验证
    /// (<c>NcwWidgetSmokeTests</c>,环境变量开启)。单测只核对它自包含、消息名与 <see cref="WidgetMessages"/> 一致。</para>
    /// </summary>
    public const string Runtime = """
        (function () {
          'use strict';
          var M = {
            content: 'ncw:widget:content', theme: 'ncw:widget:theme', loading: 'ncw:widget:loading', env: 'ncw:widget:env', reply: 'ncw:widget:reply',
            viewport: 'ncw:widget:viewport',
            ready: 'ncw:widget:ready', height: 'ncw:widget:height', call: 'ncw:widget:call', wheel: 'ncw:widget:wheel'
          };
          var nonceMeta = document.querySelector('meta[name="ncw-widget-nonce"]');
          var nonce = nonceMeta ? nonceMeta.getAttribute('content') : '';
          var root = document.getElementById('ncw-root');
          var loadingEl = document.getElementById('ncw-loading');
          var bridge = typeof invokeCSharpAction === 'function' ? invokeCSharpAction : function () {};
          var nextId = 1;
          var pending = {};
          var env = { title: '', state: null, data: null, interactive: false, submitted: false, actions: [] };
          var listeners = [];
          var contentShown = false;

          function send(message) {
            try { bridge(JSON.stringify(message)); } catch (e) { /* 宿主不在了:没有谁可以告诉 */ }
          }

          // ── 高度:量 #ncw-root 的下沿(不量 body:见文件头) ──
          // rAF 合并同一帧里的多次变化;窗口被遮挡 / 不在前台时 WebView 会停掉 rAF,所以再设一个短定时器兜底(先到先发,只发一次)
          var heightScheduled = false;
          function reportHeight() {
            if (heightScheduled) { return; }
            heightScheduled = true;
            var flush = function () {
              if (!heightScheduled) { return; }
              heightScheduled = false;
              send({ type: M.height, height: Math.ceil(root.getBoundingClientRect().bottom + window.scrollY) });
            };
            requestAnimationFrame(flush);
            setTimeout(flush, 50);
          }
          if (typeof ResizeObserver === 'function') { new ResizeObserver(reportHeight).observe(root); }

          // ── 滚轮:原生 WebView 把滚轮整个吃掉,外面的转录收不到。页面自己不滚(html / body overflow:hidden),
          // 所以把「没有元素要滚、也没有脚本拦下(preventDefault,如图表缩放)」的那部分合并交给宿主,宿主滚动转录。
          // 被动监听、从不 preventDefault:页面里能滚的元素照常滚,滚到头之后才交出去(和浏览器的滚动链一致)。双指缩放(ctrlKey)不交
          var wheelQueue = [];
          var wheelScheduled = false;
          function consumedInside(target, dy) {
            for (var el = target; el && el.nodeType === 1 && el !== document.body && el !== document.documentElement; el = el.parentElement) {
              var overflowY = getComputedStyle(el).overflowY;
              if ((overflowY === 'auto' || overflowY === 'scroll' || overflowY === 'overlay') && el.scrollHeight > el.clientHeight + 1) {
                if (dy < 0 ? el.scrollTop > 0 : el.scrollTop + el.clientHeight < el.scrollHeight - 1) { return true; }
              }
            }
            return false;
          }
          function flushWheel() {
            if (!wheelScheduled) { return; }
            wheelScheduled = false;
            var dy = 0;
            for (var i = 0; i < wheelQueue.length; i++) {
              if (!wheelQueue[i].event.defaultPrevented) { dy += wheelQueue[i].dy; }
            }
            wheelQueue = [];
            if (Math.abs(dy) >= 0.5) { send({ type: M.wheel, dy: Math.round(dy * 100) / 100 }); }
          }
          window.addEventListener('wheel', function (event) {
            if (event.ctrlKey || !event.deltaY) { return; }
            var dy = event.deltaMode === 1 ? event.deltaY * 40 : event.deltaMode === 2 ? event.deltaY * 800 : event.deltaY;
            if (consumedInside(event.target, dy)) { return; }
            // 是否被页面脚本拦下要等事件分发完才知道:先排队,flush 时再看 defaultPrevented
            wheelQueue.push({ event: event, dy: dy });
            if (wheelScheduled) { return; }
            wheelScheduled = true;
            // 下一个任务就发(不等 rAF:那要多等最多一帧,滚动会跟手慢半拍);同一批里排队的事件合成一条
            setTimeout(flushWheel, 0);
          }, { passive: true });

          // ── 调用宿主 ──
          function call(method, args) {
            return new Promise(function (resolve, reject) {
              var id = nextId++;
              pending[id] = { resolve: resolve, reject: reject };
              send({ type: M.call, id: id, method: method, args: args });
            });
          }

          // ── 内容 ──
          function runScripts(list, index) {
            for (; index < list.length; index++) {
              var source = list[index];
              var script = document.createElement('script');
              for (var i = 0; i < source.attributes.length; i++) {
                var attr = source.attributes[i];
                if (attr.name.toLowerCase() !== 'nonce') { script.setAttribute(attr.name, attr.value); }
              }
              script.nonce = nonce;
              if (source.hasAttribute('src')) {
                var resume = (function (next) { return function () { runScripts(list, next); }; })(index + 1);
                script.addEventListener('load', resume);
                script.addEventListener('error', resume);
                root.appendChild(script);
                return;
              }
              script.textContent = source.textContent;
              root.appendChild(script);
            }
            reportHeight();
          }

          var rendered = false;
          function showContent(html) {
            if (typeof html !== 'string' || rendered) { return; }
            rendered = true;
            contentShown = true;
            loadingEl.hidden = true;
            var template = document.createElement('template');
            template.innerHTML = html;
            var scripts = Array.prototype.slice.call(template.content.querySelectorAll('script'));
            scripts.forEach(function (s) { s.parentNode.removeChild(s); });
            root.textContent = '';
            root.appendChild(template.content);
            reportHeight();
            runScripts(scripts, 0);
          }

          function applyTheme(message) {
            var tokens = message.tokens;
            if (tokens && typeof tokens === 'object') {
              var style = document.documentElement.style;
              Object.keys(tokens).forEach(function (name) {
                var value = tokens[name];
                if (/^--[a-z0-9-]{1,48}$/.test(name) && typeof value === 'string' && value.length <= 400) { style.setProperty(name, value); }
              });
            }
            document.documentElement.style.colorScheme = message.appearance === 'light' ? 'light' : 'dark';
            reportHeight();
          }

          function applyEnv(next) {
            if (!next || typeof next !== 'object') { return; }
            var before = JSON.stringify(env.data);
            env = {
              title: typeof next.title === 'string' ? next.title : '',
              state: next.state === undefined ? null : next.state,
              data: next.data === undefined ? null : next.data,
              interactive: next.interactive === true,
              submitted: next.submitted === true,
              actions: Array.isArray(next.actions) ? next.actions : []
            };
            if (env.data !== null && JSON.stringify(env.data) !== before) {
              listeners.slice().forEach(function (cb) { try { cb(env.data); } catch (e) { /* 回调自己的错 */ } });
            }
          }

          function applyReply(message) {
            var entry = pending[message.id];
            if (!entry) { return; }
            delete pending[message.id];
            if (message.ok === true) { entry.resolve(message.value === undefined ? null : message.value); }
            else { entry.reject(new Error(typeof message.error === 'string' ? message.error : 'failed')); }
          }

          var host = {
            recv: function (message) {
              if (!message || typeof message !== 'object') { return; }
              switch (message.type) {
                case M.content: showContent(message.html); break;
                case M.theme: applyTheme(message); break;
                case M.loading:
                  if (typeof message.message === 'string' && !contentShown) { loadingEl.textContent = message.message; reportHeight(); }
                  break;
                case M.env: applyEnv(message.env); break;
                case M.reply: applyReply(message); break;
                case M.viewport:
                  if (typeof message.top === 'number' && isFinite(message.top)) { window.scrollTo(0, Math.max(0, message.top)); }
                  break;
              }
            }
          };
          Object.defineProperty(window, '__ncwHost', { value: Object.freeze(host) });

          // ── window.ncw:给 widget 的宿主 API ──
          var ncw = {
            getState: function () { return env.state; },
            setState: function (value) { env.state = value === undefined ? null : value; return call('setState', { state: env.state }); },
            submit: function (value) { return call('submit', { value: typeof value === 'string' ? value : JSON.stringify(value) }); },
            runAction: function (id, params) { return call('runAction', { id: String(id), params: params || {} }); },
            openUrl: function (url) { return call('openUrl', { url: String(url) }); },
            getData: function () { return env.data; },
            onData: function (callback) {
              if (typeof callback !== 'function') { return function () {}; }
              listeners.push(callback);
              if (env.data !== null) { try { callback(env.data); } catch (e) { /* 回调自己的错 */ } }
              return function () { listeners = listeners.filter(function (cb) { return cb !== callback; }); };
            },
            actions: function () { return env.actions.slice(); },
            isInteractive: function () { return env.interactive; },
            isSubmitted: function () { return env.submitted; },
            title: function () { return env.title; }
          };
          Object.defineProperty(window, 'ncw', { value: Object.freeze(ncw) });

          // 链接点击:转成 openUrl(宿主弹确认框)。导航本身不会发生——宿主会把所有导航拦下
          document.addEventListener('click', function (event) {
            var anchor = event.target && event.target.closest ? event.target.closest('a[href]') : null;
            if (!anchor) { return; }
            var href = anchor.getAttribute('href') || '';
            if (href.charAt(0) === '#') { return; }
            event.preventDefault();
            if (/^https?:\/\//i.test(href)) { call('openUrl', { url: href }).catch(function () {}); }
          }, true);

          send({ type: M.ready });
        })();
        """;
}
