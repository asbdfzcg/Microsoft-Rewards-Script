function envStrFrom(sourceEnv, key) {
    const v = sourceEnv[key]
    if (v === undefined) return undefined
    const t = String(v).trim()
    return t.length ? t : undefined
}

/**
 * Returns the configured accounts without exposing passwords, recovery
 * addresses, TOTP secrets, or proxy credentials. This API is intended for the
 * local dashboard, so account email addresses are returned in full.
 */
export function loadAccounts(sourceEnv = process.env) {
    const accounts = []
    for (let i = 1; ; i++) {
        const email = envStrFrom(sourceEnv, `ACCOUNT_${i}_EMAIL`)
        if (!email) break

        const proxyUrl = envStrFrom(sourceEnv, `ACCOUNT_${i}_PROXY_URL`)
        accounts.push({
            index: i,
            email,
            emailKey: email, // internal history join key; removed before returning the response
            geoLocale: envStrFrom(sourceEnv, `ACCOUNT_${i}_GEO_LOCALE`) ?? 'auto',
            langCode: envStrFrom(sourceEnv, `ACCOUNT_${i}_LANG_CODE`) ?? 'en',
            hasRecoveryEmail: Boolean(envStrFrom(sourceEnv, `ACCOUNT_${i}_RECOVERY_EMAIL`)),
            hasTotp: Boolean(envStrFrom(sourceEnv, `ACCOUNT_${i}_TOTP_SECRET`)),
            proxy: proxyUrl
                ? {
                    url: proxyUrl,
                    port: envStrFrom(sourceEnv, `ACCOUNT_${i}_PROXY_PORT`) ?? null,
                    hasCredentials: Boolean(envStrFrom(sourceEnv, `ACCOUNT_${i}_PROXY_USERNAME`))
                }
                : null
        })
    }
    return accounts
}

// Kept as a compatibility alias for code that imported the old function name.
export const loadAccountsMasked = loadAccounts

/**
 * Builds a child-process-only environment override that runs exactly one
 * configured account. The selected slot is remapped to ACCOUNT_1_* because the
 * bot reads account slots sequentially and stops at the first missing email.
 * No secret values leave the API process.
 */
export function buildSingleAccountEnv(accountIndex, sourceEnv = process.env) {
    const index = Number(accountIndex)
    if (!Number.isSafeInteger(index) || index < 1) {
        const err = new Error('`accountIndex` must be a positive integer.')
        err.code = 'BAD_REQUEST'
        throw err
    }

    const selectedPrefix = `ACCOUNT_${index}_`
    const selected = Object.entries(sourceEnv).filter(([key]) => key.startsWith(selectedPrefix))
    const email = envStrFrom(sourceEnv, `${selectedPrefix}EMAIL`)
    if (!email) {
        const err = new Error(`ACCOUNT_${index} is not configured.`)
        err.code = 'BAD_REQUEST'
        throw err
    }

    const env = {}

    // Blank every configured account variable in the child environment first.
    // Empty strings are treated as unset by the bot's env parser.
    for (const key of Object.keys(sourceEnv)) {
        if (/^ACCOUNT_\d+_/.test(key)) env[key] = ''
    }

    // Copy the chosen slot into slot 1, including any future ACCOUNT_N_* fields
    // not known by this API yet (password, browser settings, proxy fields, etc.).
    for (const [key, value] of selected) {
        const suffix = key.slice(selectedPrefix.length)
        env[`ACCOUNT_1_${suffix}`] = value
    }

    return {
        env,
        account: { index, email }
    }
}

export function mergeAccountStats(accounts, runs) {
    // Index history results by email.
    const byEmail = new Map()
    for (const run of runs) {
        const when = run.endedAt || run.startedAt || null
        for (const acc of run.accounts || []) {
            if (!byEmail.has(acc.email)) byEmail.set(acc.email, [])
            byEmail.get(acc.email).push({ ...acc, when })
        }
    }

    return accounts.map(a => {
        const results = byEmail.get(a.emailKey) || [] // already most-recent-first
        const last = results[0] || null

        let totalCollected = 0
        for (const r of results) totalCollected += r.collected || 0

        // Consecutive successes from the most recent run backwards.
        let successStreak = 0
        for (const r of results) {
            if (r.success === true) successStreak++
            else break
        }

        const { emailKey, ...safe } = a
        void emailKey
        return {
            ...safe,
            runs: results.length,
            totalCollected,
            successStreak,
            lastRunAt: last?.when ?? null,
            lastCollected: last?.collected ?? null,
            lastSuccess: last ? last.success : null,
            lastError: last?.error ?? null
        }
    })
}
