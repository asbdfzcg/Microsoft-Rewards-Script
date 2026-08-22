/**
 * i18n.ts - 日志中文化翻译模块（针对 v4.2.x 重写）
 *
 * 将 Logger 输出的英文标题、平台、级别、key 名和常见消息片段翻译为中文。
 * 日志和推送通知共用同一套字符串，因此两者同时中文化。
 * 翻译覆盖率取决于映射表完整度；未匹配的部分保留英文原文。
 *
 * 注意：日志过滤（consoleLogFilter / webhookLogFilter）在翻译之前对英文原文执行，
 * 因此过滤器中的 keywords 仍需配置英文关键词。
 */

// ============================================================
//  日志级别
// ============================================================
const LEVEL_MAP: Record<string, string> = {
    INFO: '信息',
    WARN: '警告',
    ERROR: '错误',
    DEBUG: '调试'
}

// ============================================================
//  平台标签
// ============================================================
const PLATFORM_MAP: Record<string, string> = {
    MAIN: '主程序',
    MOBILE: '移动端',
    DESKTOP: '桌面端'
}

// ============================================================
//  日志标题（v4.2.x 源码全量提取）
// ============================================================
const TITLE_MAP: Record<string, string> = {
    'LOGIN': '登录',
    'SEARCH-BING': 'Bing搜索',
    'BROWSER': '浏览器',
    'READ-TO-EARN': '阅读赚积分',
    'LOGIN-BING': 'Bing会话验证',
    'GET-REWARD-SESSION': '获取奖励会话',
    'URL-REWARD': '链接奖励',
    'REACT-PARSE': '页面解析',
    'ACTIVITY': '活动',
    'BUILD': '构建',
    'BOOTSTRAP': '初始化',
    'FLOW': '流程',
    'DAILY-CHECK-IN': '每日签到',
    'PUNCHCARD': '打卡任务',
    'CLOSE-BROWSER': '关闭浏览器',
    'SEARCH-MANAGER': '搜索管理',
    'MORE-PROMOTIONS': '更多推广',
    'DAILY-SET': '每日任务集',
    'CLAIM-BONUS-POINTS': '领取奖励积分',
    'RUN-START': '运行开始',
    'ACCOUNT-START': '账户开始',
    'LOGIN-APP': 'App登录',
    'GET-APP-TOKEN': '获取App令牌',
    'POINTS': '积分概览',
    'ENABLE-STREAK-PROTECTION': '连击保护',
    'APP-PROMOTIONS': '应用推广',
    'QUERY-MANAGER': '查询管理',
    'RUN-END': '运行结束',
    'ACCOUNT-END': '账户结束',
    'GET-DASHBOARD-DATA': '获取面板数据',
    'PROCESS': '进程',
    'GET-BROWSER-EARNABLE-POINTS': '获取可赚积分',
    // v4.2 新增
    'CLUSTER-PRIMARY': '主进程',
    'CLUSTER-WORKER-START': '工作进程启动',
    'CLUSTER-WORKER-DISCONNECT': '工作进程断开',
    'MAIN-ERROR': '主程序错误',
    'UNCAUGHT-EXCEPTION': '未捕获异常',
    'UNHANDLED-REJECTION': '未处理Promise拒绝',
    'BROWSER-FINGERPRINT': '浏览器指纹',
    'VISUAL-SEARCH': '视觉搜索',
    'VISUAL-SEARCH-BCID': '视觉搜索BCID',
    'DISMISS-ALL-MESSAGES': '关闭弹窗',
    'GET-NEW-TAB': '获取新标签页',
    'RELOAD-BAD-PAGE': '重载异常页面',
    'SEARCH-CLOSE-TABS': '关闭搜索标签页',
    'DETECT-STATE': '状态检测',
    'HANDLE-STATE': '状态处理',
    'LOGIN-CODE': '验证码登录',
    'LOGIN-ENTER-EMAIL': '输入邮箱',
    'LOGIN-ENTER-PASSWORD': '输入密码',
    'LOGIN-PASSWORDLESS': '免密登录',
    'LOGIN-RECOVERY': '恢复邮箱登录',
    'LOGIN-TOTP': 'TOTP验证',
    'QUERY-CLUSTER': '查询聚类',
    'SEARCH-GOOGLE-TRENDS': '谷歌趋势',
    'SEARCH-RSS': 'RSS搜索',
    'SEARCH-ON-BING-QUERY': 'Bing搜索词',
    'SEARCH-ON-BING-SEARCH': 'Bing搜索执行',
    'APP-REWARD': '应用奖励',
    'CLAIM-REWARD': '领取奖励',
    // v4.3.0 新增
    'ACCOUNT-DELAY': '账户延迟',
    'EDGE-BROWSING': 'Edge浏览',
    'EXPERIMENTAL': '实验性功能',
    'GEO-LOCALE': '地区设置',
    'COOKIE-SYNC': 'Cookie同步',
    'USER-AGENT-UPDATE': '浏览器标识更新',
    'USERAGENT-EDGE-VERSION': 'Edge版本标识',
    'USERAGENT-CHROME-VERSION': 'Chrome版本标识',
    'SEARCH-WIKIPEDIA-TRENDING': '维基趋势搜索',
    'SEARCH-WIKIPEDIA-RANDOM': '维基随机搜索',
    'SEARCH-REDDIT': 'Reddit搜索',
    'SEARCH-RANDOM-SCROLL': '随机滚动搜索',
    'SEARCH-RANDOM-CLICK': '随机点击搜索',
    'SEARCH-LOCAL-QUERY-LIST': '本地查询列表',
    'SEARCH-HACKERNEWS': 'HackerNews搜索',
    'SEARCH-BING-SUGGESTIONS': 'Bing建议搜索',
    'SEARCH-BING-RELATED': 'Bing相关搜索',
    'BROWSER-SECURITY': '浏览器安全',
    'DISABLE-FIDO': '禁用FIDO',
    'GHOST-CLICK': '幽灵点击',
    'REWARDS-PAGE': '奖励页面',
    'EARN-SNAPSHOT': '积分快照',
    'SEARCH-REPORT': '搜索报告',
    'VISUAL-SEARCH-REPORT': '视觉搜索报告',
    'ACTIVATE-SEARCH-PERK': '激活搜索特权',
    'SEARCH-ON-BING-ACTIVATE': 'Bing搜索激活',
    'GET-CURRENT-POINTS': '获取当前积分',
    'GET-APP-EARNABLE-POINTS': '获取应用可赚积分',
    'GET-APP-DASHBOARD-DATA': '获取应用面板数据',
    'CLUSTER-WORKER-TASK': '工作进程任务',
    'CLUSTER-WORKER-ERROR': '工作进程错误',
    'CLUSTER-WORKER-EXIT': '工作进程退出',
    'ACCOUNT-ERROR': '账户错误'
}

// ============================================================
//  key=value 中的 key 名
// ============================================================
const KEY_MAP: Record<string, string> = {
    currentBalance: '当前余额',
    pointsGained: '获得积分',
    remaining: '剩余',
    query: '搜索词',
    status: '状态码',
    reportable: '可报告',
    offerId: '活动ID',
    geo: '地区',
    available: '可用',
    actions: '动作数',
    previousBalance: '之前余额',
    account: '账户',
    title: '标题',
    offers: '活动数',
    streakProtectionEnabled: '连击保护已启用',
    id: '构建ID',
    streakProtectionRemainingDays: '连击保护剩余天数',
    streaks: '连击数',
    streakCounter: '连击计数',
    level: '等级',
    parents: '父任务数',
    incomplete: '未完成数',
    mobile: '移动端',
    desktop: '桌面端',
    delayRange: '延迟范围',
    type: '类型',
    edge: 'Edge',
    articlesRead: '已读文章数',
    count: '数量',
    runtimeMinutes: '运行分钟',
    accountsProcessed: '已处理账户数',
    searches: '搜索次数',
    bonus: '奖励',
    total: '总计',
    article: '文章',
    durationSeconds: '耗时秒数',
    acknowledged: '已确认',
    sc: '状态码',
    qs: '查询数',
    v: '版本',
    geoLocale: '地区设置',
    cg: '类目组'
}

// ============================================================
//  常见消息片段（按长度降序排列后替换，避免短词误伤）
// ============================================================
const MSG_FRAGMENTS: Array<[string, string]> = [
    ['Could not verify Rewards Dashboard, assuming login valid', '无法验证奖励面板，假定登录有效'],
    ['Could not verify Bing session, continuing anyway', '无法验证Bing会话，继续执行'],
    ['Daily Check-In completed but no points gained', '每日签到完成但未获得积分'],
    ['No points gained, stopping Read to Earn', '未获得积分，停止阅读赚积分'],
    ['No protection days remaining - toggle is disabled, skipping', '无连击保护天数，开关已禁用，跳过'],
    ['Logged into Microsoft Rewards successfully', '已成功登录Microsoft Rewards'],
    ['Starting Microsoft Rewards Script', '开始运行Microsoft Rewards脚本'],
    ['Passwordless authentication completed successfully', '免密认证已成功完成'],
    ['Code authentication completed successfully', '验证码认证已成功完成'],
    ['Email authentication completed successfully', '邮箱认证已成功完成'],
    ['TOTP authentication completed successfully', 'TOTP认证已成功完成'],
    ['All browser resources closed', '所有浏览器资源已关闭'],
    ['Browser context closed', '浏览器上下文已关闭'],
    ['Starting Bing session verification', '开始验证Bing会话'],
    ['Requesting mobile access token', '请求移动端访问令牌'],
    ['Mobile access token received', '移动端访问令牌已获取'],
    ['Bootstrapping rewards context', '初始化奖励上下文'],
    ['Bing session verified successfully', 'Bing会话验证成功'],
    ['Login completed, session saved', '登录完成，会话已保存'],
    ['Starting bonus search farming', '开始奖励搜索任务'],
    ['Desktop Browser started', '桌面浏览器已启动'],
    ['Mobile Browser started', '移动浏览器已启动'],
    ['items have already been completed', '项目已完成'],
    ['No search points to earn, skipping', '无搜索积分可赚，跳过'],
    ['No actionable quests', '无可执行任务'],
    ['Completed Bing searches', 'Bing搜索完成'],
    ['Starting Bing searches', '开始Bing搜索'],
    ['Search points remaining', '剩余搜索积分'],
    ['Starting Read to Earn', '开始阅读赚积分'],
    ['Completed Read to Earn', '完成阅读赚积分'],
    ['Starting Daily Check-In', '开始每日签到'],
    ['Completed Daily Check-In', '完成每日签到'],
    ['Starting login process', '开始登录'],
    ['Successfully logged in', '登录成功'],
    ['Starting session for', '开始会话'],
    ['Acquiring rewards context', '获取奖励上下文'],
    ['Points collected', '积分已收集'],
    ['Completed all accounts', '所有账户处理完成'],
    ['Primary process started', '主进程已启动'],
    ['Completed account', '账户处理完成'],
    ['Renderer crashed', '渲染进程崩溃'],
    ['Query pool ready', '查询词池就绪'],
    ['An error occurred', '发生错误'],
    ['Search summary', '搜索汇总'],
    ['Snapshot complete', '快照完成'],
    ['Context ready', '上下文就绪'],
    ['Rewards build', '奖励构建'],
    ['Nothing claimed', '无可领取'],
    ['Found activity type', '发现活动类型'],
    ['Finalizing login', '完成登录中'],
    ['Verifying Bing session', '正在验证Bing会话'],
    ['Browser launch failed', '浏览器启动失败'],
    ['Browser started', '浏览器已启动'],
    ['Earnable today', '今日可赚'],
    ['Started solving', '开始处理'],
    ['Unknown state at', '未知状态'],
    ['Starting account', '开始处理账户'],
    ['Starting UrlReward', '开始链接奖励'],
    ['Completed UrlReward', '完成链接奖励'],
    ['Entering password', '正在输入密码'],
    ['Entering email', '正在输入邮箱'],
    ['Worker spawned', '工作进程已创建'],
    ['Quest list', '任务列表'],
    ['Accounts:', '账户数:'],
    ['Clusters:', '集群数:'],
    ['skip (no points)', '跳过（无积分）'],
    ['Read article', '阅读文章'],
    ['User-Agent:', '浏览器标识:'],
    ['geoLocale:', '地区:'],
    ['headless:', '无头模式:'],
    ['platform:', '平台:'],
    ['proxy:', '代理:'],
    ['waiting', '等待中']
]

// 按英文长度降序排列，避免短词先匹配导致长词断裂
const SORTED_FRAGMENTS = [...MSG_FRAGMENTS].sort((a, b) => b[0].length - a[0].length)

// ============================================================
//  导出的翻译函数
// ============================================================

export function translateLevel(level: string): string {
    return LEVEL_MAP[level] || level
}

export function translatePlatform(platform: string): string {
    return PLATFORM_MAP[platform] || platform
}

export function translateTitle(title: string): string {
    return TITLE_MAP[title] || title
}

/**
 * 翻译消息体（含 key=value 对和文本片段）。
 * 策略：按 ' | ' 分割，每段先尝试 key 翻译，失败则做文本片段翻译。
 */
export function translateBody(body: string): string {
    if (!body) return body
    const parts = body.split(' | ')
    if (parts.length === 1) {
        return translateFragments(body)
    }
    const translated = parts.map(part => {
        const trimmed = part.trim()
        const eqIdx = trimmed.indexOf('=')
        if (eqIdx !== -1) {
            const key = trimmed.substring(0, eqIdx)
            const val = trimmed.substring(eqIdx + 1)
            const zhKey = KEY_MAP[key]
            if (zhKey) return `${zhKey}=${val}`
        }
        return translateFragments(part)
    })
    return translated.join(' | ')
}

function translateFragments(text: string): string {
    let result = text
    for (const [en, zh] of SORTED_FRAGMENTS) {
        if (result.includes(en)) {
            result = result.split(en).join(zh)
        }
    }
    return result
}
