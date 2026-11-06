import type { WhimsyBucket } from '../../../shared/domain/activity'

/**
 * 状态行上那句会动的话 —— 按**此刻在干什么**分组的料。
 *
 * 起因是一次 run 里最没东西可看的那几十秒:一句「正在等待回复…」或「运行中」
 * 从第一秒到第四十秒一个像素都不变,读起来像卡住了,而不像在干活。
 * 轮换一个词不提供任何新信息,但它证明这一帧是刚画出来的;而**按场景分组**
 * 之后它还真多说了一件事 —— 眼下是在翻文件、在跑命令,还是在派帮手;
 * 是第一次跑它,还是连着第五次;上一步是不是刚红了一片。
 *
 * ★ 分组的粒度跟着 `WhimsyBucket` 走,不自己另立一套。谁压过谁见那边的
 * `whimsyBucketOf` —— 这里只负责**一个分组里的料**。
 *
 * ★ 只当**纯装饰**用:真正的状态在 `data-status` 属性上,读屏念的是那句固定的
 * `chat.status.waitingResponse` / `chat.status.running`(见 StatusLine 里的 sr-only)。
 * 所以这里既不进 Messages 表、也不参与 ZH/EN 键一致性校验 —— 它不是要翻译的文案,
 * 是一串可以随便增删的料。两边不必一一对应,某一组多几条少几条都行。
 *
 * ★ **每条都要短。** 这句话右边紧跟着 `· 队列 2` 和用量读数,
 * 词一长后面整排就跟着左右抖。中文压在 6 字以内,英文压在 14 字符以内
 * (`i18n/index.test.ts` 钉着这两个数)。**吐槽也得在这个预算里** ——
 * 装不下的那句,多半是想在状态行上讲一个只有写的人才懂的笑话。
 *
 * ★ **不描述它不知道的事。** 这一句拿不到文件名、命令、错误内容,
 * 所以只说「在做这一类事」,不说「在做这一件事」;工具卡片才是说那个的地方。
 */
type Whimsy = readonly [string, ...string[]]

export const whimsyZh: Record<WhimsyBucket, Whimsy> = {
  // ── 没有工具在跑的几种时刻 ───────────────────────────────
  // 等首字节:纯粹的干等,什么都还没发生
  waiting: [
    '琢磨中…', '盘算中…', '酝酿中…', '推敲中…', '捣鼓中…', '掐指一算…',
    '打草稿…', '打腹稿…', '理思路…', '搭架子…', '翻资料…', '找灵感…',
    '转脑筋…', '绕圈圈…', '踱步中…', '熬汤中…', '咕嘟咕嘟…', '发酵中…',
    '磨刀中…', '憋大招…', '挠头中…', '眉头一皱…', '冥思苦想…', '搜肠刮肚…',
    '脑内开会…', '排列组合…', '掂量掂量…', '让我想想…'
  ],
  // 思考块在流:和干等不同,这时候是**看得见在想**,词跟着往「推演」上靠
  reasoning: [
    '顺着想…', '反着想…', '再想想…', '想深一层…', '推演中…', '权衡中…',
    '找漏洞…', '自我辩论…', '反复横跳…', '钻牛角尖…', '理因果…', '解扣中…'
  ],
  /*
    对话内生视频。★ 与生图分开:这一段描述的是**后台在跑一个几分钟的任务**,
    而"生成中"那个词放在视频上比放在图片上更准(用户确实要等很久)。
    必须有这一项 —— `WhimsyBucket` 是从 `ToolShape` 派生的,新增形态会编译期要求它。
  */
  video: [
    '生成视频…', '渲染中…', '合成中…', '出片中…', '剪片中…', '等渲染…',
    '还在画…', '帧在排队…', '导出中…', '出成片…'
  ],
  // 正文在流:已经开始写了
  writing: [
    '下笔中…', '码字中…', '打字中…', '组织语言…', '斟酌用词…', '一字一句…',
    '润色中…', '边写边想…', '落笔了…', '收尾中…'
  ],
  // ── 相位兜底:工具认不出来(MCP / 插件 / 新工具)时走这里 ────
  read: [
    '读文件…', '翻页中…', '扫读中…', '逐行看…', '一目十行…', '啃文件…',
    '目不转睛…', '翻开一看…', '对照着看…', '划重点…'
  ],
  search: [
    '大海捞针…', '顺藤摸瓜…', '按图索骥…', '翻箱倒柜…', '刨根问底…', '满仓库找…',
    '挨个翻…', '循迹中…', '找找看…', '地毯式搜…'
  ],
  mutate: [
    '动刀中…', '施工中…', '改稿中…', '拧螺丝…', '缝缝补补…', '修修改改…',
    '大兴土木…', '搬砖中…', '落笔改…', '收拾残局…'
  ],
  command: [
    '敲回车…', '开跑了…', '等回显…', '盯着输出…', '噼里啪啦…', '跑一趟…',
    '命令下去…', '等它跑完…', '咔咔咔…', '连敲带跑…'
  ],
  network: [
    '上网中…', '拉数据…', '等响应…', '蹲网页…', '翻网页…', '抓取中…',
    '连出去了…', '等对面…', '排队等网…', '拨号中…'
  ],
  orchestration: [
    '派活中…', '摇人中…', '点将中…', '分头行动…', '叫帮手…', '分工中…',
    '远程指挥…', '盯梢中…', '排兵布阵…', '等回话…'
  ],
  // 等用户表态:这一档里 Agent 其实**不在干活**,词得说清是在等人,不是在忙
  interaction: [
    '等你一句…', '等你拍板…', '球在你那…', '等你点头…', '等你选…', '候着呢…'
  ],
  external: [
    '喊外援…', '对接中…', '借力中…', '走外线…', '联络中…', '接上了…',
    '跨界调用…', '递话中…', '搭桥中…'
  ],
  // 画图:这一档用户**看得见东西在长**,所以词往"手上在做"上靠,
  // 而不是"在想"(那是 waiting/reasoning 的活)
  widget: [
    '画图中…', '勾勒中…', '描线中…', '配色中…', '布局中…', '打样中…',
    '排布中…', '上色中…'
  ],
  // 生图:等的是上游把整张图交回来 —— 用户在卡片上看着加载格,词往"出片"上靠
  image: [
    '出图中…', '显影中…', '调色中…', '构图中…', '落笔中…', '冲洗中…',
    '洗照片…', '等出片…'
  ],
  // 页面截图:等的是浏览器把当前视口交回来,词往"按快门"上靠
  screenshot: [
    '截图…', '瞄准中…', '取景中…', '对焦中…', '按下快门…', '定格里…',
    '咔嚓…', '成像中…'
  ],
  // 工作流:一群子代理在按图干活,词往"调度/编排"上靠
  workflow: [
    '编排中…', '调度中…', '分派中…', '并行推进…', '接力中…', '汇总中…',
    '排兵布阵…', '流水作业…'
  ],
  // 兜底的兜底:知道在跑,但说不出在跑什么(参数还在流、工具还没开跑)
  working: [
    '忙活中…', '开工中…', '动手中…', '撸起袖子…', '跑腿中…', '连轴转…',
    '马不停蹄…', '火力全开…', '开足马力…', '手脚并用…', '埋头苦干…', '一顿操作…',
    '三下五除二…', '哼哧哼哧…', '叮叮当当…', '收拾中…', '折腾中…', '加急处理…'
  ],
  // ── 具体工具 ─────────────────────────────────────────
  'read.file': [
    '翻文件…', '逐行看…', '啃文档…', '划重点…', '一目十行…', '看看写了啥',
    '对着源码…', '又一个文件', '读到哪了…', '盯着看…'
  ],
  'read.dir': [
    '数文件…', '翻目录…', '清点中…', '看看有啥…', '逛一圈…', '目录巡检…',
    '挨个点名…', '摸清家底…'
  ],
  'mutate.write': [
    '落笔了…', '刷刷刷…', '新建中…', '写下去…', '成文中…', '存盘中…',
    '一气呵成…', '从头写起…', '敲定了…'
  ],
  'mutate.edit': [
    '动刀中…', '拧螺丝…', '缝补中…', '改这行…', '微调中…', '精雕细琢…',
    '手术中…', '就差一点…', '挪两行…'
  ],
  'search.glob': [
    '按名字找…', '撒网中…', '铺开找…', '对模式…', '摸文件…', '列个名单…',
    '挨着筛…'
  ],
  'search.grep': [
    '大海捞针…', '顺藤摸瓜…', '翻箱倒柜…', '刨根问底…', '地毯式搜…', '挨行看…',
    '有没有呢…', '满仓库找…', '循迹中…'
  ],
  'command.bash': [
    '敲回车…', '开跑了…', '等回显…', '噼里啪啦…', '盯输出…', '跑一趟…',
    '祈祷中…', '但愿能过…', '等它跑完…', '咔咔咔…'
  ],
  'network.fetch': [
    '蹲网页…', '抓过来…', '等对面…', '拨号中…', '读网页…', '拉数据…',
    '等加载…', '排队等网…'
  ],
  'network.search': [
    '搜一搜…', '问问外网…', '翻搜索页…', '海选中…', '筛结果…', '看看别人…',
    '广撒网…'
  ],
  'plan.todo': [
    '列清单…', '划勾中…', '排顺序…', '记一笔…', '对进度…', '又加一条…',
    '理待办…'
  ],
  'delegate.subagent': [
    '摇人中…', '点将中…', '派活出去…', '等徒弟…', '分头行动…', '远程指挥…',
    '交给它了…', '坐等回话…'
  ],
  skill: [
    '翻手册…', '查招式…', '照方抓药…', '按谱来…', '取经中…', '照着做…'
  ],
  schedule: [
    '定闹钟…', '对表中…', '排日程…', '掐时间…', '写进日历…', '设时辰…'
  ],
  // ── 场景 ────────────────────────────────────────────
  // 久等:与其继续轮换「琢磨中…」装作一切正常,不如承认它确实慢
  'slow.wait': [
    '还没来…', '再等等…', '望眼欲穿…', '有点久了…', '催一催…', '人呢…',
    '等到花谢…', '还在路上…'
  ],
  'slow.tool': [
    '还在跑…', '有点久…', '慢工出活…', '别急别急…', '快了快了…', '进度条呢',
    '再等一会…', '它没停…'
  ],
  // 同一个工具连着第 N 次 —— 用户已经在时间线上看见那一摞一样的行了
  grind: [
    '没完没了…', '又来一遍…', '还是它…', '一遍又一遍', '重复劳动…', '愚公移山…',
    '不知疲倦…', '再来一次…'
  ],
  // 上一个工具红了 —— 说一句比假装无事发生强
  recover: [
    '刚翻车了…', '收拾残局…', '换个法子…', '再试一次…', '止损中…', '补救中…',
    '刚踩坑了…', '重新来过…'
  ],
  'context.tight': [
    '脑容量告急', '记不下了…', '塞满了…', '该清理了…', '超载中…', '快装不下'
  ],
  'context.compacting': [
    '整理记忆…', '压缩中…', '腾地方…', '打包旧账…', '精简中…', '归拢中…'
  ]
}

export const whimsyEn: Record<WhimsyBucket, Whimsy> = {
  waiting: [
    'Pondering…', 'Percolating…', 'Noodling…', 'Ruminating…', 'Musing…', 'Brewing…',
    'Simmering…', 'Steeping…', 'Incubating…', 'Cogitating…', 'Puzzling…', 'Mulling…',
    'Marinating…', 'Tinkering…', 'Conjuring…', 'Untangling…', 'Scheming…', 'Plotting…',
    'Whirring…', 'Daydreaming…', 'Doodling…', 'Sketching…', 'Squinting…', 'Wondering…',
    'Weighing…', 'Chewing…', 'Pacing…', 'Humming…'
  ],
  reasoning: [
    'Reasoning…', 'Deducing…', 'Inferring…', 'Unpacking…', 'Backtracking…', 'Rechecking…',
    'Zooming in…', 'Arguing…', 'Doubting…', 'Connecting…'
  ],
  /*
    Video generation. It runs for minutes in the background, so the words are about
    rendering/exporting rather than "drawing".
  */
  /*
    ★ 英文压在 14 字符以内(`i18n/index.test.ts` 钉着这个数):
    这句话右边紧跟着「· 队列 2」和用量读数,长一个字符整排就左右抖。
  */
  video: [
    'Rendering…', 'Encoding…', 'Compositing…', 'Frames…', 'Cutting…',
    'Waiting…', 'Still going…', 'Exporting…', 'Almost…', 'Assembling…'
  ],
  writing: [
    'Writing…', 'Drafting…', 'Typing…', 'Composing…', 'Phrasing…', 'Wording it…',
    'Penning…', 'Polishing…', 'Wrapping up…'
  ],
  read: [
    'Reading…', 'Skimming…', 'Scanning…', 'Perusing…', 'Leafing…', 'Poring over…',
    'Eyeballing…', 'Flipping…', 'Absorbing…'
  ],
  search: [
    'Searching…', 'Hunting…', 'Combing…', 'Rummaging…', 'Digging…', 'Sleuthing…',
    'Sniffing…', 'Trawling…', 'Prowling…'
  ],
  mutate: [
    'Editing…', 'Patching…', 'Rewiring…', 'Stitching…', 'Whittling…', 'Hammering…',
    'Chiseling…', 'Reshaping…', 'Tidying…', 'Retooling…'
  ],
  command: [
    'Running…', 'Executing…', 'Invoking…', 'Crunching…', 'Chugging…', 'Clacking…',
    'Piping…', 'Watching…', 'Churning…', 'Firing away…'
  ],
  network: [
    'Fetching…', 'Browsing…', 'Surfing…', 'Pinging…', 'Loading…', 'Downloading…',
    'Dialing out…', 'Awaiting…', 'Knocking…', 'Reeling…'
  ],
  orchestration: [
    'Delegating…', 'Dispatching…', 'Rallying…', 'Herding…', 'Deputizing…',
    'Marshalling…', 'Checking in…', 'Handing off…', 'Regrouping…'
  ],
  interaction: [
    'Your call…', 'Over to you…', 'Waiting on you', 'Asking you…', 'Your turn…'
  ],
  external: [
    'Calling out…', 'Bridging…', 'Relaying…', 'Plugging in…', 'Negotiating…',
    'Patching in…', 'Hailing…', 'Linking up…'
  ],
  widget: [
    'Sketching…', 'Drawing…', 'Plotting…', 'Charting…', 'Composing…',
    'Rendering…', 'Coloring…', 'Diagramming…'
  ],
  image: [
    'Painting…', 'Developing…', 'Framing…', 'Exposing…', 'Imagining…',
    'Brushing…', 'Composing…', 'Printing…'
  ],
  screenshot: [
    'Capturing…', 'Aiming…', 'Framing…', 'Focusing…', 'Shutter…', 'Snapping…',
    'Click…', 'Freezing it…'
  ],
  workflow: [
    'Orchestrating…', 'Dispatching…', 'Fanning out…', 'In parallel…', 'Relaying…',
    'Merging…', 'Arranging…', 'Pipelining…'
  ],
  working: [
    'Working…', 'Wrangling…', 'Hustling…', 'Cranking…', 'Grinding…', 'Wrenching…',
    'Shuffling…', 'Shoveling…', 'Hauling…', 'Barreling…', 'Bustling…', 'Juggling…',
    'Sprinting…', 'Clattering…', 'Assembling…', 'Splicing…'
  ],
  'read.file': [
    'Reading…', 'Line by line…', 'Skimming…', 'Poring over…', 'Another file…',
    'Absorbing…', 'Marking it up…', 'Squinting…'
  ],
  'read.dir': [
    'Listing…', 'Counting files', 'Peeking in…', 'Taking stock…', 'Browsing…',
    'Sizing it up…'
  ],
  'mutate.write': [
    'Writing it…', 'Penning…', 'Laying it down', 'Saving…', 'From scratch…',
    'Committing…', 'Drafting…'
  ],
  'mutate.edit': [
    'Editing…', 'Patching…', 'Splicing…', 'Nudging lines…', 'Rewiring…',
    'Fine-tuning…', 'Almost…', 'Tweaking…'
  ],
  'search.glob': [
    'Globbing…', 'By name…', 'Casting a net…', 'Matching…', 'Listing hits…',
    'Sifting…'
  ],
  'search.grep': [
    'Grepping…', 'Combing…', 'Digging…', 'Needle hunt…', 'Rummaging…',
    'Any luck?…', 'Line by line…', 'Trawling…'
  ],
  'command.bash': [
    'Running it…', 'Hit enter…', 'Watching…', 'Clacking…', 'Hoping…',
    'Tailing logs…', 'Exit code?…', 'Churning…'
  ],
  'network.fetch': [
    'Fetching…', 'Loading…', 'Dialing out…', 'Pulling bytes…', 'Knocking…',
    'Their turn…'
  ],
  'network.search': [
    'Searching…', 'Sifting hits…', 'Asking around', 'Scanning web…', 'Casting wide…',
    'Skimming…'
  ],
  'plan.todo': [
    'Listing todos…', 'Ticking off…', 'Reordering…', 'Noting it…', 'One more item',
    'Replanning…'
  ],
  'delegate.subagent': [
    'Delegating…', 'Rallying…', 'Sending help…', 'Deputizing…', 'Checking in…',
    'Handing off…', 'Their turn…'
  ],
  skill: [
    'Reading docs…', 'Consulting…', 'By the book…', 'Looking it up…', 'Following…'
  ],
  schedule: [
    'Setting time…', 'Checking clock', 'Calendaring…', 'Pinning a time', 'Timing it…'
  ],
  'slow.wait': [
    'Still nothing…', 'Any moment…', 'Tapping foot…', 'Hello?…', 'Waiting…',
    'Long silence…'
  ],
  'slow.tool': [
    'Still going…', 'Taking a while', 'Hang on…', 'Almost…', 'Long one…',
    'Not done yet…'
  ],
  grind: [
    'Again?…', 'And again…', 'Once more…', 'Groundhog day', 'On repeat…',
    'Tireless…'
  ],
  recover: [
    'That broke…', 'Cleaning up…', 'Plan B…', 'Retrying…', 'Rerouting…',
    'Second try…', 'Recovering…'
  ],
  'context.tight': [
    'Brain is full…', 'Running out…', 'Memory tight…', 'Too much…', 'Nearly full…'
  ],
  'context.compacting': [
    'Compacting…', 'Tidying memory', 'Making room…', 'Summarizing…', 'Folding it…'
  ]
}
