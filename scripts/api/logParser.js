const ANSI_RE = /\u001B\[[0-9;]*m/g

export function stripAnsi(str) {
    return typeof str === 'string' ? str.replace(ANSI_RE, '') : str
}

const LINE_RE = /^\[([^\]]*)\] \[([^\]]*)\] \[(INFO|WARN|ERROR|DEBUG)\] (MAIN|MOBILE|DESKTOP) \[([^\]]*)\] ([\s\S]*)$/

const SEVERITY = { debug: 0, info: 1, warn: 2, error: 3 }

export function severityRank(level) {
    return SEVERITY[level] ?? 1
}

export function parseLogLine(rawInput, source = 'stdout') {
    const raw = stripAnsi(String(rawInput))
    const match = raw.match(LINE_RE)

    if (match) {
        const [, ts, user, levelTag, platform, title, message] = match
        return {
            ts,
            level: levelTag.toLowerCase(),
            user: user || null,
            platform,
            title,
            message,
            source,
            parsed: true,
            raw
        }
    }

    let level = source === 'stderr' ? 'error' : 'info'
    if (/\b(ERROR|Error:|ERR!|FATAL|Traceback|Unhandled)\b/.test(raw)) level = 'error'
    else if (/\b(WARN|WARNING|Deprecat)/i.test(raw)) level = 'warn'

    return {
        ts: null,
        level,
        user: null,
        platform: null,
        title: null,
        message: raw,
        source,
        parsed: false,
        raw
    }
}

export function createRunState() {
    return {
        version: null,
        clusters: null,
        accountsTotal: null,
        currentEmail: null,
        userToEmail: {}, // log "user" (email localpart) -> full email, for attributing live lines
        totals: null, // { collected, oldTotal, newTotal, runtimeMinutes, accountsProcessed }
        order: [], // emails in the order they started
        accounts: {}, // email -> account summary
        errors: [], // recent error/warn messages { ts, level, title, message }
        finished: false
    }
}

function ensureAccount(state, email) {
    if (!email) return null
    if (!state.accounts[email]) {
        state.accounts[email] = {
            email,
            geoLocale: null,
            initialPoints: null,
            collectedPoints: null,
            finalPoints: null,
            earnable: null, // { mobile, browser, app } as reported in the "Earnable today" line
            searchSummary: null, // { mobile, desktop, bonus, total }
            durationSeconds: null,
            success: null,
            error: null,
            live: {
                balance: null, // latest known available-points balance
                gained: 0, // points earned so far this run (per this account)
                bySource: {}, // { search, bonus, read, checkIn, claimReward, claimBonus }
                lastUpdateTs: null
            }
        }
        state.order.push(email)
    }
    return state.accounts[email]
}

const RE = {
    runStart: /^Starting Microsoft Rewards Script \| v(\S+) \| Accounts: (\d+) \| Clusters: (\d+)/,
    accountStart: /^Starting account: (\S+) \| geoLocale: (.+?)\s*$/,
    earnable: /^Earnable today \| Mobile: (\d+) \| Browser: (\d+) \| App: (\d+) \| (\S+) \| locale: (\S+)/,
    searchSummary: /^Search summary \| mobile=(-?\d+) \| desktop=(-?\d+) \| bonus=(-?\d+) \| total=(-?\d+)/,
    accountEnd: /^Completed account: (\S+) \| Total: \+(-?\d+) \| Old: (\d+) → New: (\d+) \| Duration: ([\d.]+)s/,
    runEnd: /^Completed all accounts \| Accounts processed: (\d+) \| Total points collected: \+(-?\d+) \| Old total: (\d+) → New total: (\d+) \| Total runtime: ([\d.]+)min/,
    accountError: /^(\S+@\S+): ([\s\S]+)$/,
    flowFailed: /flow failed for (\S+@\S+):/i,

    searchStart: /^Starting Bing searches \| currentPoints=(\d+)/,
    searchApiGain: /^gainedPoints=(\d+) \| query=".*?" \| balance=(\S+) \| searchPts=/,
    searchBrowserGain: /^\+(\d+) \| query="/,
    readGain: /^Read article \d+\/\d+ \| status=\S+ \| gainedPoints=(\d+) \| newBalance=(\d+)/,
    checkInGain: /Completed Daily Check-In \| type=103 \| gainedPoints=(\d+) \| oldBalance=\d+ \| newBalance=(\d+)/,
    claimBonusGain: /^Completed ClaimBonusPoints \| acknowledged=true(?: \| gainedPoints=(\d+))? \| newBalance=(\d+)/,
    claimRewardGain: /^Reward claimed \| offerId=\S+ \| status=\S+(?: \| gainedPoints=(\d+))?/,
    flowCollected: /^Collected: \+(-?\d+) \| (\S+@\S+)/
}

function applyLivePoints(state, entry) {
    const msg = entry.message ?? ''

    const emailFromUser = user => (user ? state.userToEmail[user] : null)
    const target = email => ensureAccount(state, email || emailFromUser(entry.user) || state.currentEmail)
    const num = s => {
        const n = Number(s)
        return Number.isFinite(n) ? n : null
    }
    const touch = acc => {
        acc.live.lastUpdateTs = entry.ts
    }
    const setBalance = (acc, balance) => {
        if (!acc || balance == null) return false
        if (acc.live.balance === balance) return false
        acc.live.balance = balance
        touch(acc)
        return true
    }
    const addGain = (acc, gained, balance, source) => {
        if (!acc) return false
        let changed = false
        if (balance != null && acc.live.balance !== balance) {
            acc.live.balance = balance
            changed = true
        }
        if (gained > 0) {
            acc.live.gained += gained
            acc.live.bySource[source] = (acc.live.bySource[source] || 0) + gained
            changed = true
        }
        if (changed) touch(acc)
        return changed
    }

    let m
    switch (entry.title) {
        case 'SEARCH-BING':
        case 'SEARCH-BONUS':
            if ((m = msg.match(RE.searchStart))) return setBalance(target(), num(m[1]))
            if ((m = msg.match(RE.searchApiGain))) return addGain(target(), Number(m[1]), num(m[2]), 'search')
            if ((m = msg.match(RE.searchBrowserGain)))
                return addGain(target(), Number(m[1]), null, entry.title === 'SEARCH-BONUS' ? 'bonus' : 'search')
            return false

        case 'READ-TO-EARN':
            if ((m = msg.match(RE.readGain))) return addGain(target(), Number(m[1]), num(m[2]), 'read')
            return false

        case 'DAILY-CHECK-IN':
            if ((m = msg.match(RE.checkInGain))) return addGain(target(), Number(m[1]), num(m[2]), 'checkIn')
            return false

        case 'CLAIM-BONUS-POINTS':
            if ((m = msg.match(RE.claimBonusGain))) return addGain(target(), m[1] ? Number(m[1]) : 0, num(m[2]), 'claimBonus')
            return false

        case 'CLAIM-REWARD':
            if ((m = msg.match(RE.claimRewardGain))) return addGain(target(), m[1] ? Number(m[1]) : 0, null, 'claimReward')
            return false

        case 'FLOW':
            if ((m = msg.match(RE.flowCollected))) {
                const acc = target(m[2])
                if (!acc) return false
                const total = Number(m[1])
                if (acc.live.gained === total) return false
                acc.live.gained = total
                touch(acc)
                return true
            }
            return false

        default:
            return false
    }
}

export function applyLogToRunState(state, entry) {
    const msg = entry.message ?? ''

    if (entry.level === 'error' || entry.level === 'warn') {
        state.errors.push({
            ts: entry.ts,
            level: entry.level,
            title: entry.title,
            message: msg
        })
        if (state.errors.length > 200) state.errors.shift()

        const ff = msg.match(RE.flowFailed)
        if (ff) {
            const acc = ensureAccount(state, ff[1])
            if (acc) {
                acc.error = msg
                acc.success = acc.success === true ? true : false
            }
        }
    }

    if (!entry.parsed) return null

    if (applyLivePoints(state, entry)) return 'points'

    let m
    switch (entry.title) {
        case 'RUN-START':
            if ((m = msg.match(RE.runStart))) {
                state.version = m[1]
                state.accountsTotal = Number(m[2])
                state.clusters = Number(m[3])
                state.finished = false
                return 'run-start'
            }
            break

        case 'ACCOUNT-START':
            if ((m = msg.match(RE.accountStart))) {
                const acc = ensureAccount(state, m[1])
                if (acc) acc.geoLocale = m[2]
                state.currentEmail = m[1]
                if (entry.user) state.userToEmail[entry.user] = m[1] // map localpart -> full email
                return 'account-start'
            }
            break

        case 'POINTS':
            if ((m = msg.match(RE.earnable))) {
                const email = m[4]
                const acc = ensureAccount(state, email)
                if (acc) {
                    acc.earnable = { mobile: Number(m[1]), browser: Number(m[2]), app: Number(m[3]) }
                }
                state.currentEmail = email
            }
            break

        case 'SEARCH-MANAGER':
            if ((m = msg.match(RE.searchSummary)) && state.currentEmail) {
                const acc = ensureAccount(state, state.currentEmail)
                if (acc) {
                    acc.searchSummary = {
                        mobile: Number(m[1]),
                        desktop: Number(m[2]),
                        bonus: Number(m[3]),
                        total: Number(m[4])
                    }
                }
            }
            break

        case 'ACCOUNT-END':
            if ((m = msg.match(RE.accountEnd))) {
                const acc = ensureAccount(state, m[1])
                if (acc) {
                    acc.collectedPoints = Number(m[2])
                    acc.initialPoints = Number(m[3])
                    acc.finalPoints = Number(m[4])
                    acc.durationSeconds = Number(m[5])
                    acc.success = true
                    acc.live.gained = Number(m[2])
                    acc.live.balance = Number(m[4])
                }
                return 'account-end'
            }
            break

        case 'ACCOUNT-ERROR':
            if ((m = msg.match(RE.accountError))) {
                const acc = ensureAccount(state, m[1])
                if (acc) {
                    acc.error = m[2].trim()
                    acc.success = false
                }
                return 'account-error'
            }
            break

        case 'RUN-END':
            if ((m = msg.match(RE.runEnd))) {
                state.totals = {
                    accountsProcessed: Number(m[1]),
                    collected: Number(m[2]),
                    oldTotal: Number(m[3]),
                    newTotal: Number(m[4]),
                    runtimeMinutes: Number(m[5])
                }
                state.finished = true
                return 'run-end'
            }
            break

        default:
            break
    }

    return null
}

function accountCollected(a) {
    if (typeof a.collectedPoints === 'number') return a.collectedPoints
    return a.live?.gained ?? 0
}

export function summarizeRunState(state) {
    const accounts = state.order.map(email => state.accounts[email])
    const collected = state.totals?.collected ?? accounts.reduce((sum, a) => sum + accountCollected(a), 0)

    const current = state.currentEmail ? state.accounts[state.currentEmail] : null
    let lastUpdateTs = null
    for (const a of accounts) {
        if (a.live?.lastUpdateTs) lastUpdateTs = a.live.lastUpdateTs
    }

    return {
        version: state.version,
        clusters: state.clusters,
        accountsTotal: state.accountsTotal,
        accountsSeen: accounts.length,
        collected,
        totals: state.totals,
        finished: state.finished,
        live: {
            currentAccount: state.currentEmail,
            currentBalance: current?.live?.balance ?? null,
            gained: collected,
            updatedAt: lastUpdateTs
        },
        accounts
    }
}
