/**
 * UpdateChecker.ts - 版本更新检查器
 *
 * 脚本启动时静默检查 GitHub 最新 Release，结果写入 autorun/update-status.json，
 * 由 RewardsManager.exe 读取并提示用户更新。
 * 任何网络/解析失败都不会影响主流程，仅记录 error 字段。
 */

import fs from 'node:fs'
import path from 'node:path'
import https from 'node:https'
import pkg from '../../package.json'

const REPO = 'asbdfzcg/Microsoft-Rewards-Script'
const CHECK_TIMEOUT_MS = 8000

export interface UpdateStatus {
    currentVersion: string
    latestVersion: string | null
    updateAvailable: boolean
    skippedVersion: string | null
    changelog: string | null
    releaseUrl: string | null
    publishedAt: string | null
    checkedAt: string
    error: string | null
}

interface GithubRelease {
    tag_name?: string
    body?: string
    html_url?: string
    published_at?: string
}

function normalizeVersion(v: string | undefined | null): string {
    return (v || '').trim().replace(/^v/i, '')
}

/** 简单版本比较：a > b 返回正数，a < b 返回负数，相等返回 0 */
function compareVersions(a: string, b: string): number {
    const pa = normalizeVersion(a).split('.').map(n => parseInt(n, 10) || 0)
    const pb = normalizeVersion(b).split('.').map(n => parseInt(n, 10) || 0)
    for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
        const diff = (pa[i] || 0) - (pb[i] || 0)
        if (diff !== 0) return diff
    }
    return 0
}

function fetchLatestRelease(rejectUnauthorized = true): Promise<GithubRelease> {
    return new Promise((resolve, reject) => {
        const req = https.request(
            {
                hostname: 'api.github.com',
                path: `/repos/${REPO}/releases/latest`,
                method: 'GET',
                timeout: CHECK_TIMEOUT_MS,
                rejectUnauthorized,
                headers: {
                    'User-Agent': `Microsoft-Rewards-Script/${pkg.version}`,
                    Accept: 'application/vnd.github+json'
                }
            },
            res => {
                if (res.statusCode !== 200) {
                    res.resume()
                    reject(new Error(`GitHub API 返回状态码 ${res.statusCode}`))
                    return
                }
                let data = ''
                res.on('data', chunk => (data += chunk))
                res.on('end', () => {
                    try {
                        resolve(JSON.parse(data) as GithubRelease)
                    } catch (e) {
                        reject(e)
                    }
                })
            }
        )
        req.on('timeout', () => req.destroy(new Error('请求超时')))
        req.on('error', reject)
        req.end()
    })
}

function readJsonSafe<T>(file: string): T | null {
    try {
        if (fs.existsSync(file)) {
            return JSON.parse(fs.readFileSync(file, 'utf-8')) as T
        }
    } catch {}
    return null
}

/**
 * 静默检查更新（fire-and-forget）。
 * 结果写入 <项目根>/autorun/update-status.json；用户跳过的版本记录于 update-skipped.json。
 */
export async function checkForUpdates(): Promise<void> {
    const projectRoot = path.resolve(__dirname, '..', '..')
    const autorunDir = path.join(projectRoot, 'autorun')
    const statusFile = path.join(autorunDir, 'update-status.json')
    const skippedFile = path.join(autorunDir, 'update-skipped.json')

    const status: UpdateStatus = {
        currentVersion: normalizeVersion(pkg.version),
        latestVersion: null,
        updateAvailable: false,
        skippedVersion: null,
        changelog: null,
        releaseUrl: null,
        publishedAt: null,
        checkedAt: new Date().toISOString(),
        error: null
    }

    try {
        fs.mkdirSync(autorunDir, { recursive: true })

        const skipped = readJsonSafe<{ skippedVersion?: string }>(skippedFile)
        status.skippedVersion = skipped?.skippedVersion ? normalizeVersion(skipped.skippedVersion) : null

        let release: GithubRelease
        try {
            release = await fetchLatestRelease(true)
        } catch (err) {
            const msg = err instanceof Error ? err.message : String(err)
            if (msg.includes('unable to verify the first certificate')) {
                release = await fetchLatestRelease(false)
            } else {
                throw err
            }
        }
        const latest = normalizeVersion(release.tag_name)
        status.latestVersion = latest || null
        status.changelog = release.body || null
        status.releaseUrl = release.html_url || null
        status.publishedAt = release.published_at || null

        if (latest && compareVersions(latest, status.currentVersion) > 0 && latest !== status.skippedVersion) {
            status.updateAvailable = true
        }
    } catch (error) {
        status.error = error instanceof Error ? error.message : String(error)
    }

    try {
        fs.writeFileSync(statusFile, JSON.stringify(status, null, 2), 'utf-8')
    } catch {}
}
