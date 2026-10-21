(function () {
    'use strict';

    if (window.__JellyfinSeriesDownloaderLoaded) {
        return;
    }
    window.__JellyfinSeriesDownloaderLoaded = true;

    const QUALITY_PRESETS = [
        {
            id: 'original',
            name: 'Original Quality (Direct File / No Transcoding)',
            badge: 'Lossless',
            isOriginal: true
        },
        {
            id: '2160p-20m',
            name: '4K UHD — 2160p (20 Mbps)',
            badge: '3840×2160',
            isOriginal: false,
            maxWidth: 3840,
            maxHeight: 2160,
            videoBitrateKbps: 20000,
            audioBitrateKbps: 320
        },
        {
            id: '1080p-8m',
            name: '1080p Full HD High (8 Mbps)',
            badge: '1920×1080',
            isOriginal: false,
            maxWidth: 1920,
            maxHeight: 1080,
            videoBitrateKbps: 8000,
            audioBitrateKbps: 192
        },
        {
            id: '1080p-4m',
            name: '1080p Full HD Balanced (4 Mbps)',
            badge: '1920×1080',
            isOriginal: false,
            maxWidth: 1920,
            maxHeight: 1080,
            videoBitrateKbps: 4000,
            audioBitrateKbps: 192
        },
        {
            id: '720p-2.5m',
            name: '720p HD Standard (2.5 Mbps)',
            badge: '1280×720',
            isOriginal: false,
            maxWidth: 1280,
            maxHeight: 720,
            videoBitrateKbps: 2500,
            audioBitrateKbps: 160
        },
        {
            id: '720p-1.5m',
            name: '720p HD Compact (1.5 Mbps)',
            badge: '1280×720',
            isOriginal: false,
            maxWidth: 1280,
            maxHeight: 720,
            videoBitrateKbps: 1500,
            audioBitrateKbps: 128
        },
        {
            id: '480p-1m',
            name: '480p SD (1 Mbps)',
            badge: '854×480',
            isOriginal: false,
            maxWidth: 854,
            maxHeight: 480,
            videoBitrateKbps: 1000,
            audioBitrateKbps: 128
        },
        {
            id: '360p-500k',
            name: '360p Mobile Saver (500 kbps)',
            badge: '640×360',
            isOriginal: false,
            maxWidth: 640,
            maxHeight: 360,
            videoBitrateKbps: 500,
            audioBitrateKbps: 96
        },
        {
            id: 'custom',
            name: 'Custom Quality (Any Resolution / Bitrate / CRF / Codec)',
            badge: 'Custom',
            isOriginal: false,
            maxWidth: 1920,
            maxHeight: 1080,
            videoBitrateKbps: 5000,
            audioBitrateKbps: 192
        }
    ];

    let pollIntervalId = null;
    let activeModalItemId = null;
    let currentTree = null;
    let autoDownloadedJobs = new Set();

    function escapeHtml(val) {
        if (val === null || val === undefined) return '';
        return String(val)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function getApiUrl(path) {
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            return window.ApiClient.getUrl(path);
        }
        return '/' + path.replace(/^\/+/, '');
    }

    async function apiFetch(path, options = {}) {
        const url = getApiUrl(path);
        const method = (options.method || 'GET').toUpperCase();

        if (window.ApiClient && typeof window.ApiClient.ajax === 'function') {
            const ajaxOpts = {
                type: method,
                url: url,
                dataType: 'json'
            };
            if (options.body && typeof options.body === 'object') {
                ajaxOpts.data = JSON.stringify(options.body);
                ajaxOpts.contentType = 'application/json';
            }
            try {
                return await window.ApiClient.ajax(ajaxOpts);
            } catch (err) {
                let errMsg = `HTTP ${err && err.status ? err.status : 'Error'}`;
                if (err && typeof err.json === 'function') {
                    try {
                        const errJson = await err.json();
                        if (errJson && (errJson.Message || errJson.message)) {
                            errMsg = errJson.Message || errJson.message;
                        }
                    } catch (_) {}
                } else if (err && typeof err.text === 'function') {
                    try {
                        const raw = await err.text();
                        const parsed = JSON.parse(raw);
                        if (parsed && (parsed.Message || parsed.message)) {
                            errMsg = parsed.Message || parsed.message;
                        }
                    } catch (_) {}
                }
                throw new Error(errMsg);
            }
        }

        const headers = Object.assign({}, options.headers || {});
        let body = options.body;
        if (body && typeof body === 'object') {
            headers['Content-Type'] = 'application/json';
            body = JSON.stringify(body);
        }
        const response = await fetch(url, {
            method,
            headers,
            body,
            credentials: 'same-origin'
        });
        if (!response.ok) {
            let errText = `HTTP ${response.status}`;
            try {
                const rawText = await response.text();
                if (rawText) {
                    const errJson = JSON.parse(rawText);
                    if (errJson && (errJson.Message || errJson.message || errJson.title)) {
                        errText = errJson.Message || errJson.message || errJson.title;
                    }
                }
            } catch (_) {
                // ignore parse errors
            }
            throw new Error(errText);
        }
        const contentType = response.headers.get('content-type') || '';
        if (contentType.includes('application/json')) {
            return await response.json();
        }
        return await response.text();
    }

    function formatBytes(bytes) {
        if (!bytes || bytes <= 0) return '—';
        const units = ['B', 'KB', 'MB', 'GB', 'TB'];
        let i = 0;
        let val = bytes;
        while (val >= 1024 && i < units.length - 1) {
            val /= 1024;
            i++;
        }
        return `${val.toFixed(i >= 2 ? 2 : 0)} ${units[i]}`;
    }

    function formatDurationTicks(ticks) {
        if (!ticks || ticks <= 0) return '';
        const totalSeconds = Math.round(ticks / 10000000);
        const mins = Math.floor(totalSeconds / 60);
        if (mins >= 60) {
            const hrs = Math.floor(mins / 60);
            const remMins = mins % 60;
            return `${hrs}h ${remMins}m`;
        }
        return `${ Math.max(1, mins) }m`;
    }

    function injectStyles() {
        if (document.getElementById('jsd-styles')) return;
        const style = document.createElement('style');
        style.id = 'jsd-styles';
        style.textContent = `
            .jsd-modal-backdrop {
                position: fixed;
                inset: 0;
                background: rgba(0, 0, 0, 0.78);
                backdrop-filter: blur(6px);
                z-index: 99999;
                display: flex;
                align-items: center;
                justify-content: center;
                padding: 16px;
                font-family: inherit;
                color: #f3f4f6;
            }
            .jsd-modal {
                background: #18181b;
                border: 1px solid rgba(255,255,255,0.12);
                border-radius: 12px;
                width: 100%;
                max-width: 840px;
                max-height: 90vh;
                display: flex;
                flex-direction: column;
                box-shadow: 0 25px 50px -12px rgba(0,0,0,0.75);
                overflow: hidden;
            }
            .jsd-modal-header {
                display: flex;
                align-items: center;
                justify-content: space-between;
                padding: 18px 24px;
                background: #202024;
                border-bottom: 1px solid rgba(255,255,255,0.08);
            }
            .jsd-modal-title {
                font-size: 1.2rem;
                font-weight: 600;
                display: flex;
                align-items: center;
                gap: 10px;
                margin: 0;
            }
            .jsd-close-btn {
                background: transparent;
                border: none;
                color: #9ca3af;
                font-size: 1.5rem;
                cursor: pointer;
                padding: 4px 8px;
                border-radius: 6px;
                line-height: 1;
            }
            .jsd-close-btn:hover {
                color: #fff;
                background: rgba(255,255,255,0.08);
            }
            .jsd-tabs {
                display: flex;
                background: #202024;
                padding: 0 24px;
                gap: 20px;
                border-bottom: 1px solid rgba(255,255,255,0.08);
            }
            .jsd-tab {
                background: none;
                border: none;
                color: #9ca3af;
                padding: 10px 4px;
                font-size: 0.95rem;
                font-weight: 500;
                cursor: pointer;
                border-bottom: 2px solid transparent;
            }
            .jsd-tab.active {
                color: #00a4dc;
                border-bottom-color: #00a4dc;
            }
            .jsd-modal-body {
                padding: 20px 24px;
                overflow-y: auto;
                flex: 1;
                display: flex;
                flex-direction: column;
                gap: 18px;
            }
            .jsd-section-label {
                font-size: 0.85rem;
                text-transform: uppercase;
                letter-spacing: 0.05em;
                color: #9ca3af;
                margin-bottom: 8px;
                font-weight: 600;
            }
            .jsd-grid-2 {
                display: grid;
                grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
                gap: 12px;
            }
            .jsd-grid-3 {
                display: grid;
                grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
                gap: 12px;
            }
            .jsd-field {
                display: flex;
                flex-direction: column;
                gap: 5px;
            }
            .jsd-field label {
                font-size: 0.82rem;
                color: #d1d5db;
            }
            .jsd-input, .jsd-select {
                background: #27272a;
                border: 1px solid rgba(255,255,255,0.15);
                border-radius: 6px;
                color: #fff;
                padding: 9px 11px;
                font-size: 0.92rem;
                outline: none;
            }
            .jsd-input:focus, .jsd-select:focus {
                border-color: #00a4dc;
            }
            .jsd-advanced-box {
                background: #202025;
                border: 1px solid rgba(255,255,255,0.08);
                border-radius: 8px;
                padding: 14px;
                display: flex;
                flex-direction: column;
                gap: 12px;
            }
            .jsd-episodes-toolbar {
                display: flex;
                align-items: center;
                justify-content: space-between;
                flex-wrap: wrap;
                gap: 8px;
                margin-bottom: 6px;
            }
            .jsd-seasons-container {
                border: 1px solid rgba(255,255,255,0.1);
                border-radius: 8px;
                max-height: 270px;
                overflow-y: auto;
                background: #141417;
            }
            .jsd-season-block {
                border-bottom: 1px solid rgba(255,255,255,0.06);
            }
            .jsd-season-header {
                display: flex;
                align-items: center;
                justify-content: space-between;
                padding: 10px 14px;
                background: #1f1f23;
                cursor: pointer;
                font-weight: 600;
                font-size: 0.93rem;
            }
            .jsd-episode-list {
                padding: 6px 14px 10px 28px;
                display: flex;
                flex-direction: column;
                gap: 6px;
            }
            .jsd-episode-row {
                display: flex;
                align-items: center;
                justify-content: space-between;
                font-size: 0.88rem;
                padding: 4px 6px;
                border-radius: 4px;
            }
            .jsd-episode-row:hover {
                background: rgba(255,255,255,0.04);
            }
            .jsd-ep-meta {
                display: flex;
                gap: 8px;
                color: #9ca3af;
                font-size: 0.78rem;
                align-items: center;
            }
            .jsd-badge {
                background: rgba(0, 164, 220, 0.16);
                color: #38bdf8;
                padding: 2px 7px;
                border-radius: 4px;
                font-size: 0.75rem;
                font-weight: 500;
            }
            .jsd-modal-footer {
                padding: 14px 24px;
                background: #202024;
                border-top: 1px solid rgba(255,255,255,0.08);
                display: flex;
                align-items: center;
                justify-content: space-between;
                flex-wrap: wrap;
                gap: 12px;
            }
            .jsd-btn {
                border: none;
                border-radius: 6px;
                padding: 9px 16px;
                font-size: 0.9rem;
                font-weight: 600;
                cursor: pointer;
                display: inline-flex;
                align-items: center;
                gap: 7px;
                transition: filter 0.15s;
            }
            .jsd-btn:hover {
                filter: brightness(1.1);
            }
            .jsd-btn-primary {
                background: #00a4dc;
                color: #fff;
            }
            .jsd-btn-secondary {
                background: #3f3f46;
                color: #f4f4f5;
            }
            .jsd-btn-success {
                background: #10b981;
                color: #fff;
            }
            .jsd-btn-danger {
                background: #ef4444;
                color: #fff;
                padding: 5px 10px;
                font-size: 0.8rem;
            }
            .jsd-btn-sm {
                padding: 5px 10px;
                font-size: 0.8rem;
            }
            .jsd-job-card {
                background: #222227;
                border: 1px solid rgba(255,255,255,0.09);
                border-radius: 8px;
                padding: 14px;
                display: flex;
                flex-direction: column;
                gap: 8px;
            }
            .jsd-progress-track {
                width: 100%;
                height: 8px;
                background: #3f3f46;
                border-radius: 99px;
                overflow: hidden;
            }
            .jsd-progress-fill {
                height: 100%;
                background: linear-gradient(90deg, #00a4dc, #38bdf8);
                transition: width 0.3s ease;
            }
            .jsd-floating-pill {
                position: fixed;
                bottom: 22px;
                right: 22px;
                background: #18181b;
                border: 1px solid #00a4dc;
                color: #fff;
                padding: 10px 16px;
                border-radius: 999px;
                box-shadow: 0 10px 25px rgba(0,0,0,0.6);
                z-index: 99990;
                cursor: pointer;
                display: flex;
                align-items: center;
                gap: 10px;
                font-size: 0.88rem;
            }
        `;
        document.head.appendChild(style);
    }

    function getCurrentItemIdFromUrl() {
        const hash = window.location.hash || '';
        const search = window.location.search || '';
        const combined = hash + '&' + search;
        const match = combined.match(/[?&]id=([a-fA-F0-9-]{32,36})/);
        return match ? match[1] : null;
    }

    function tryInjectDetailButton() {
        const activePages = document.querySelectorAll('.itemDetailPage:not(.hide), #itemDetailPage:not(.hide)');
        if (!activePages.length) return;

        const page = activePages[activePages.length - 1];
        const buttonContainer = page.querySelector('.mainDetailButtons, .detailButtons');
        if (!buttonContainer) return;

        const itemId = getCurrentItemIdFromUrl();
        if (!itemId) return;

        let existingBtn = buttonContainer.querySelector('.btnSeriesQualityDownload');
        if (existingBtn) {
            if (existingBtn.getAttribute('data-item-id') !== itemId) {
                existingBtn.setAttribute('data-item-id', itemId);
            }
            return;
        }

        const btn = document.createElement('button');
        btn.setAttribute('is', 'emby-button');
        btn.setAttribute('type', 'button');
        btn.setAttribute('data-item-id', itemId);
        btn.className = 'button-flat btnSeriesQualityDownload detailButton emby-button';
        btn.title = 'Download Series / Season / Episode in Any Quality';
        btn.innerHTML = `
            <div class="detailButton-content">
                <span class="material-icons detailButton-icon download" aria-hidden="true"></span>
                <span class="button-text">Download Quality</span>
            </div>
        `;

        btn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            const targetId = btn.getAttribute('data-item-id') || getCurrentItemIdFromUrl();
            if (targetId) {
                openDownloadModal(targetId);
            }
        });

        buttonContainer.appendChild(btn);
    }

    async function openDownloadModal(itemId, initialTab = 'configure') {
        injectStyles();
        activeModalItemId = itemId;

        let backdrop = document.getElementById('jsd-modal-backdrop');
        if (backdrop) backdrop.remove();

        backdrop = document.createElement('div');
        backdrop.id = 'jsd-modal-backdrop';
        backdrop.className = 'jsd-modal-backdrop';
        backdrop.innerHTML = `
            <div class="jsd-modal" role="dialog" aria-modal="true">
                <div class="jsd-modal-header">
                    <h2 class="jsd-modal-title">
                        <span class="material-icons" style="color:#00a4dc">download</span>
                        <span id="jsd-header-title">Series Quality Downloader</span>
                    </h2>
                    <button type="button" class="jsd-close-btn" id="jsd-close-modal" title="Close">&times;</button>
                </div>
                <div class="jsd-tabs">
                    <button type="button" class="jsd-tab ${initialTab === 'configure' ? 'active' : ''}" data-tab="configure">Configure Download</button>
                    <button type="button" class="jsd-tab ${initialTab === 'jobs' ? 'active' : ''}" data-tab="jobs">Download Queue & Progress</button>
                </div>
                <div class="jsd-modal-body" id="jsd-modal-body">
                    <div style="padding: 30px; text-align: center; color: #9ca3af;">Loading series episodes and stream info...</div>
                </div>
                <div class="jsd-modal-footer" id="jsd-modal-footer"></div>
            </div>
        `;

        document.body.appendChild(backdrop);

        backdrop.querySelector('#jsd-close-modal').addEventListener('click', closeModal);
        backdrop.addEventListener('click', (e) => {
            if (e.target === backdrop) closeModal();
        });

        const tabs = backdrop.querySelectorAll('.jsd-tab');
        tabs.forEach(t => {
            t.addEventListener('click', () => {
                tabs.forEach(x => x.classList.remove('active'));
                t.classList.add('active');
                if (t.dataset.tab === 'configure') {
                    renderConfigureTab();
                } else {
                    renderJobsTab();
                }
            });
        });

        if (initialTab === 'jobs') {
            renderJobsTab();
            if (itemId) {
                apiFetch(`SeriesDownloader/Items/${itemId}/Tree`).then(tree => { currentTree = tree; }).catch(() => {});
            }
            return;
        }

        try {
            currentTree = await apiFetch(`SeriesDownloader/Items/${itemId}/Tree`);
            const headerEl = document.getElementById('jsd-header-title');
            if (headerEl && currentTree.Title) {
                headerEl.textContent = `Download: ${currentTree.Title}`;
            }
            renderConfigureTab();
        } catch (err) {
            const bodyEl = document.getElementById('jsd-modal-body');
            if (bodyEl) {
                bodyEl.innerHTML = `<div style="color:#ef4444; padding: 20px;">Could not load episodes for this item: ${escapeHtml(err.message)}</div>`;
            }
        }
    }

    function closeModal() {
        const backdrop = document.getElementById('jsd-modal-backdrop');
        if (backdrop) backdrop.remove();
    }

    function renderConfigureTab() {
        const bodyEl = document.getElementById('jsd-modal-body');
        const footerEl = document.getElementById('jsd-modal-footer');
        if (!bodyEl || !footerEl) return;

        if (!currentTree || !currentTree.Seasons || currentTree.Seasons.length === 0) {
            bodyEl.innerHTML = `<div style="padding:20px; color:#9ca3af;">No downloadable episodes found for this item.</div>`;
            footerEl.innerHTML = '';
            return;
        }

        const audioLangOptions = [
            `<option value="default">Default Audio Track</option>`,
            `<option value="all">All Audio Tracks</option>`,
            ...(currentTree.AvailableAudioLanguages || []).map(l => `<option value="${escapeHtml(l)}">Language: ${escapeHtml(String(l).toUpperCase())}</option>`)
        ].join('');

        const subLangOptions = [
            `<option value="all">All Matching Subtitles</option>`,
            ...(currentTree.AvailableSubtitleLanguages || []).map(l => `<option value="${escapeHtml(l)}">Language: ${escapeHtml(String(l).toUpperCase())}</option>`)
        ].join('');

        const detectedCodec = currentTree.DetectedVideoEncoder || 'libx264';
        const detectedSummary = escapeHtml(currentTree.DetectedHardwareSummary || 'CPU Software Encoding');

        bodyEl.innerHTML = `
            <div>
                <div class="jsd-section-label">1. Select Target Quality Preset</div>
                <div class="jsd-grid-2">
                    <div class="jsd-field">
                        <label for="jsd-preset-select">Quality Profile</label>
                        <select id="jsd-preset-select" class="jsd-select">
                            ${QUALITY_PRESETS.map((p, idx) => `<option value="${escapeHtml(p.id)}" ${idx === 2 ? 'selected' : ''}>${escapeHtml(p.name)}</option>`).join('')}
                        </select>
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-container-select">Output Container Format</label>
                        <select id="jsd-container-select" class="jsd-select">
                            <option value="mp4" selected>MP4 (Universal Compatibility)</option>
                            <option value="mkv">MKV (Matroska Multi-Track)</option>
                        </select>
                    </div>
                </div>
            </div>

            <div class="jsd-advanced-box" id="jsd-custom-quality-box">
                <div style="display:flex; justify-content:space-between; align-items:center; flex-wrap:wrap; gap:8px;">
                    <span class="jsd-section-label" style="margin:0;">2. Customize Resolution, Bitrate & Hardware Acceleration</span>
                    <div style="display:flex; gap:6px; align-items:center;">
                        <span class="jsd-badge" style="background:rgba(16,185,129,0.16); color:#34d399;" title="Auto-detected from Jellyfin Transcoding settings">${detectedSummary}</span>
                        <span class="jsd-badge" id="jsd-active-preset-badge">1920×1080</span>
                    </div>
                </div>
                <div class="jsd-grid-3">
                    <div class="jsd-field">
                        <label for="jsd-max-width">Max Width (px, blank = source)</label>
                        <input type="number" id="jsd-max-width" class="jsd-input" value="1920" min="128" max="7680" placeholder="e.g. 1920" />
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-max-height">Max Height (px, blank = source)</label>
                        <input type="number" id="jsd-max-height" class="jsd-input" value="1080" min="128" max="4320" placeholder="e.g. 1080" />
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-video-bitrate">Video Bitrate (kbps, 0 = use CRF/QP)</label>
                        <input type="number" id="jsd-video-bitrate" class="jsd-input" value="8000" min="0" max="150000" step="100" />
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-crf">Constant Quality (CRF / CQ / QP 0-51)</label>
                        <input type="number" id="jsd-crf" class="jsd-input" value="22" min="0" max="51" />
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-video-codec">Video Encoder (GPU / CPU)</label>
                        <select id="jsd-video-codec" class="jsd-select">
                            <option value="libx264" ${detectedCodec === 'libx264' ? 'selected' : ''}>H.264 (libx264 CPU)</option>
                            <option value="libx265" ${detectedCodec === 'libx265' ? 'selected' : ''}>HEVC / H.265 (libx265 CPU)</option>
                            <option value="h264_nvenc" ${detectedCodec === 'h264_nvenc' ? 'selected' : ''}>H.264 NVIDIA NVENC (GPU)</option>
                            <option value="hevc_nvenc" ${detectedCodec === 'hevc_nvenc' ? 'selected' : ''}>HEVC NVIDIA NVENC (GPU)</option>
                            <option value="h264_qsv" ${detectedCodec === 'h264_qsv' ? 'selected' : ''}>H.264 Intel QuickSync (GPU)</option>
                            <option value="hevc_qsv" ${detectedCodec === 'hevc_qsv' ? 'selected' : ''}>HEVC Intel QuickSync (GPU)</option>
                            <option value="h264_vaapi" ${detectedCodec === 'h264_vaapi' ? 'selected' : ''}>H.264 VAAPI (Intel/AMD GPU)</option>
                            <option value="hevc_vaapi" ${detectedCodec === 'hevc_vaapi' ? 'selected' : ''}>HEVC VAAPI (Intel/AMD GPU)</option>
                            <option value="copy">Direct Video Stream Copy</option>
                        </select>
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-preset-speed">Encoding Speed Preset</label>
                        <select id="jsd-preset-speed" class="jsd-select">
                            <option value="ultrafast">Ultrafast (Fastest CPU)</option>
                            <option value="superfast">Superfast</option>
                            <option value="veryfast" selected>Veryfast (Recommended)</option>
                            <option value="faster">Faster</option>
                            <option value="fast">Fast</option>
                            <option value="medium">Medium (Smaller File)</option>
                        </select>
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-audio-codec">Audio Encoder</label>
                        <select id="jsd-audio-codec" class="jsd-select">
                            <option value="aac" selected>AAC (Universal)</option>
                            <option value="libmp3lame">MP3</option>
                            <option value="libopus">Opus</option>
                            <option value="ac3">AC3 (Dolby)</option>
                            <option value="copy">Copy Original Audio</option>
                        </select>
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-audio-bitrate">Audio Bitrate (kbps)</label>
                        <input type="number" id="jsd-audio-bitrate" class="jsd-input" value="192" min="64" max="640" step="32" />
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-audio-lang">Audio Track Selection</label>
                        <select id="jsd-audio-lang" class="jsd-select">${audioLangOptions}</select>
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-sub-mode">Subtitles</label>
                        <select id="jsd-sub-mode" class="jsd-select">
                            <option value="none">No Subtitles</option>
                            <option value="embed" selected>Embed Soft Subtitles</option>
                        </select>
                    </div>
                    <div class="jsd-field">
                        <label for="jsd-sub-lang">Subtitle Language</label>
                        <select id="jsd-sub-lang" class="jsd-select">${subLangOptions}</select>
                    </div>
                    <div class="jsd-field" style="justify-content:flex-end;">
                        <label style="display:flex; align-items:center; gap:8px; cursor:pointer; padding:9px 0;">
                            <input type="checkbox" id="jsd-hw-decode" checked />
                            <span>Enable GPU Hardware Decoding (<code>-hwaccel</code>)</span>
                        </label>
                    </div>
                </div>
            </div>

            <div>
                <div class="jsd-episodes-toolbar">
                    <span class="jsd-section-label" style="margin:0;">3. Select Seasons & Episodes</span>
                    <div style="display:flex; gap:8px;">
                        <button type="button" class="jsd-btn jsd-btn-secondary jsd-btn-sm" id="jsd-select-all">Select All</button>
                        <button type="button" class="jsd-btn jsd-btn-secondary jsd-btn-sm" id="jsd-deselect-all">Deselect All</button>
                    </div>
                </div>
                <div class="jsd-seasons-container" id="jsd-seasons-tree">
                    ${currentTree.Seasons.map((season, sIdx) => `
                        <div class="jsd-season-block" data-season-idx="${sIdx}">
                            <div class="jsd-season-header">
                                <label style="display:flex; align-items:center; gap:8px; cursor:pointer;" onclick="event.stopPropagation()">
                                    <input type="checkbox" class="jsd-season-cb" data-season-idx="${sIdx}"
                                        ${season.Episodes.every(e => e.SelectedByDefault) ? 'checked' : ''} />
                                    <span>${escapeHtml(season.SeasonName)} (${season.Episodes.length} episodes)</span>
                                </label>
                                <span style="font-size:0.8rem; color:#9ca3af;">Toggle</span>
                            </div>
                            <div class="jsd-episode-list">
                                ${season.Episodes.map(ep => {
                                    const epCode = `S${String(ep.SeasonNumber || 1).padStart(2, '0')}E${String(ep.EpisodeNumber || 1).padStart(2, '0')}`;
                                    const resBadge = ep.Height ? `${ep.Height}p ${ep.VideoCodec || ''}`.trim() : (ep.VideoCodec || '');
                                    return `
                                        <label class="jsd-episode-row">
                                            <span style="display:flex; align-items:center; gap:8px;">
                                                <input type="checkbox" class="jsd-ep-cb"
                                                    data-season-idx="${sIdx}"
                                                    data-ep-id="${escapeHtml(ep.EpisodeId)}"
                                                    data-runtime-ticks="${Number(ep.RunTimeTicks) || 0}"
                                                    data-filesize="${Number(ep.FileSizeBytes) || 0}"
                                                    ${ep.SelectedByDefault ? 'checked' : ''} />
                                                <span><strong>${escapeHtml(epCode)}</strong> — ${escapeHtml(ep.Name)}</span>
                                            </span>
                                            <span class="jsd-ep-meta">
                                                ${resBadge ? `<span class="jsd-badge">${escapeHtml(resBadge)}</span>` : ''}
                                                <span>${escapeHtml(formatDurationTicks(ep.RunTimeTicks))}</span>
                                                <span>${escapeHtml(formatBytes(ep.FileSizeBytes))}</span>
                                            </span>
                                        </label>
                                    `;
                                }).join('')}
                            </div>
                        </div>
                    `).join('')}
                </div>
            </div>
        `;

        footerEl.innerHTML = `
            <div id="jsd-selection-summary" style="font-size:0.88rem; color:#d1d5db;"></div>
            <div style="display:flex; gap:10px; align-items:center;">
                <button type="button" class="jsd-btn jsd-btn-secondary" id="jsd-instant-zip-btn" style="display:none;">
                    <span class="material-icons" style="font-size:18px;">bolt</span>
                    Instant Stream .ZIP
                </button>
                <button type="button" class="jsd-btn jsd-btn-primary" id="jsd-start-job-btn">
                    <span class="material-icons" style="font-size:18px;">download</span>
                    Start Quality Download
                </button>
            </div>
        `;

        const presetSelect = document.getElementById('jsd-preset-select');
        const customBox = document.getElementById('jsd-custom-quality-box');
        const badgeEl = document.getElementById('jsd-active-preset-badge');
        const maxWidthInput = document.getElementById('jsd-max-width');
        const maxHeightInput = document.getElementById('jsd-max-height');
        const vBitrateInput = document.getElementById('jsd-video-bitrate');
        const aBitrateInput = document.getElementById('jsd-audio-bitrate');
        const instantZipBtn = document.getElementById('jsd-instant-zip-btn');

        function syncPresetFields() {
            const preset = QUALITY_PRESETS.find(p => p.id === presetSelect.value) || QUALITY_PRESETS[0];
            if (preset.isOriginal) {
                customBox.style.display = 'none';
                instantZipBtn.style.display = 'inline-flex';
            } else {
                customBox.style.display = 'flex';
                instantZipBtn.style.display = 'none';
                badgeEl.textContent = preset.badge;
                if (preset.id !== 'custom') {
                    maxWidthInput.value = preset.maxWidth || '';
                    maxHeightInput.value = preset.maxHeight || '';
                    vBitrateInput.value = preset.videoBitrateKbps || 0;
                    aBitrateInput.value = preset.audioBitrateKbps || 192;
                }
            }
            updateSelectionSummary();
        }

        function updateSelectionSummary() {
            const checked = Array.from(bodyEl.querySelectorAll('.jsd-ep-cb:checked'));
            const preset = QUALITY_PRESETS.find(p => p.id === presetSelect.value) || QUALITY_PRESETS[0];
            let estBytes = 0;

            if (preset.isOriginal) {
                estBytes = checked.reduce((acc, el) => acc + (parseInt(el.dataset.filesize || '0', 10) || 0), 0);
            } else {
                const vKbps = parseInt(vBitrateInput.value || '0', 10) || 3500;
                const aKbps = parseInt(aBitrateInput.value || '192', 10) || 192;
                const totalKbps = vKbps + aKbps;
                const totalSeconds = checked.reduce((acc, el) => acc + ((parseInt(el.dataset.runtimeTicks || '0', 10) || 0) / 10000000), 0);
                estBytes = (totalKbps * 1000 / 8) * totalSeconds;
            }

            const summaryEl = document.getElementById('jsd-selection-summary');
            if (summaryEl) {
                const packType = checked.length > 1 ? 'packaged as .ZIP' : 'single video file';
                summaryEl.innerHTML = `<strong>${checked.length}</strong> episode(s) selected (${packType}) &bull; Est. size: <strong>${formatBytes(estBytes)}</strong>`;
            }
        }

        presetSelect.addEventListener('change', syncPresetFields);
        vBitrateInput.addEventListener('input', updateSelectionSummary);
        aBitrateInput.addEventListener('input', updateSelectionSummary);

        bodyEl.querySelectorAll('.jsd-season-header').forEach(header => {
            header.addEventListener('click', () => {
                const list = header.parentElement.querySelector('.jsd-episode-list');
                if (list) {
                    list.style.display = list.style.display === 'none' ? 'flex' : 'none';
                }
            });
        });

        bodyEl.querySelectorAll('.jsd-season-cb').forEach(scb => {
            scb.addEventListener('change', () => {
                const sIdx = scb.dataset.seasonIdx;
                bodyEl.querySelectorAll(`.jsd-ep-cb[data-season-idx="${sIdx}"]`).forEach(ecb => {
                    ecb.checked = scb.checked;
                });
                updateSelectionSummary();
            });
        });

        bodyEl.querySelectorAll('.jsd-ep-cb').forEach(ecb => {
            ecb.addEventListener('change', updateSelectionSummary);
        });

        document.getElementById('jsd-select-all').addEventListener('click', () => {
            bodyEl.querySelectorAll('.jsd-ep-cb, .jsd-season-cb').forEach(cb => { cb.checked = true; });
            updateSelectionSummary();
        });

        document.getElementById('jsd-deselect-all').addEventListener('click', () => {
            bodyEl.querySelectorAll('.jsd-ep-cb, .jsd-season-cb').forEach(cb => { cb.checked = false; });
            updateSelectionSummary();
        });

        instantZipBtn.addEventListener('click', async () => {
            const ids = Array.from(bodyEl.querySelectorAll('.jsd-ep-cb:checked')).map(cb => cb.dataset.epId);
            if (!ids.length) {
                alert('Please select at least one episode.');
                return;
            }
            try {
                const res = await apiFetch('SeriesDownloader/StreamTickets', {
                    method: 'POST',
                    body: {
                        EpisodeIds: ids,
                        Title: currentTree.Title || 'Series'
                    }
                });
                if (res && res.TicketId) {
                    const streamUrl = getApiUrl(`SeriesDownloader/StreamOriginalZip/${encodeURIComponent(res.TicketId)}`);
                    triggerBrowserDownload(streamUrl);
                }
            } catch (err) {
                alert('Failed to start ZIP stream: ' + err.message);
            }
        });

        document.getElementById('jsd-start-job-btn').addEventListener('click', async () => {
            const ids = Array.from(bodyEl.querySelectorAll('.jsd-ep-cb:checked')).map(cb => cb.dataset.epId);
            if (!ids.length) {
                alert('Please select at least one episode.');
                return;
            }

            const preset = QUALITY_PRESETS.find(p => p.id === presetSelect.value) || QUALITY_PRESETS[0];
            const payload = {
                EpisodeIds: ids,
                Title: currentTree.Title,
                IsOriginalQuality: !!preset.isOriginal,
                Container: document.getElementById('jsd-container-select').value,
                MaxWidth: parseInt(maxWidthInput.value, 10) || null,
                MaxHeight: parseInt(maxHeightInput.value, 10) || null,
                VideoBitrateKbps: parseInt(vBitrateInput.value, 10) || null,
                Crf: parseInt(document.getElementById('jsd-crf').value, 10) || 22,
                VideoCodec: document.getElementById('jsd-video-codec').value,
                EnableHardwareDecoding: !!document.getElementById('jsd-hw-decode').checked,
                Preset: document.getElementById('jsd-preset-speed').value,
                AudioCodec: document.getElementById('jsd-audio-codec').value,
                AudioBitrateKbps: parseInt(aBitrateInput.value, 10) || 192,
                PreferredAudioLanguage: document.getElementById('jsd-audio-lang').value,
                SubtitleMode: document.getElementById('jsd-sub-mode').value,
                PreferredSubtitleLanguage: document.getElementById('jsd-sub-lang').value
            };

            const startBtn = document.getElementById('jsd-start-job-btn');
            startBtn.disabled = true;
            startBtn.textContent = 'Starting...';

            try {
                const job = await apiFetch('SeriesDownloader/Jobs', {
                    method: 'POST',
                    body: payload
                });

                const tabs = document.querySelectorAll('.jsd-tab');
                tabs.forEach(t => t.classList.toggle('active', t.dataset.tab === 'jobs'));
                await renderJobsTab();
                startGlobalJobPolling();

                if (job && job.State === 'Completed') {
                    autoDownloadedJobs.add(job.JobId);
                    triggerBrowserDownload(getApiUrl(`SeriesDownloader/Jobs/${encodeURIComponent(job.JobId)}/Download?token=${encodeURIComponent(job.DownloadToken || '')}`));
                }
            } catch (err) {
                alert('Failed to start download job: ' + err.message);
                startBtn.disabled = false;
                startBtn.textContent = 'Start Quality Download';
            }
        });

        syncPresetFields();
    }

    async function renderJobsTab() {
        const bodyEl = document.getElementById('jsd-modal-body');
        const footerEl = document.getElementById('jsd-modal-footer');
        if (!bodyEl || !footerEl) return;

        footerEl.innerHTML = `
            <span style="font-size:0.84rem; color:#9ca3af;">Jobs continue running in the background if you close this modal.</span>
            <button type="button" class="jsd-btn jsd-btn-secondary jsd-btn-sm" id="jsd-refresh-jobs">Refresh</button>
        `;
        const refreshBtn = document.getElementById('jsd-refresh-jobs');
        if (refreshBtn) refreshBtn.addEventListener('click', renderJobsTab);

        try {
            const jobs = await apiFetch('SeriesDownloader/Jobs');
            if (!jobs || jobs.length === 0) {
                bodyEl.innerHTML = `<div style="padding:30px; text-align:center; color:#9ca3af;">No active or recent download jobs.</div>`;
                return;
            }

            bodyEl.innerHTML = jobs.map(job => {
                const isDone = job.State === 'Completed';
                const isActive = job.State === 'Queued' || job.State === 'Transcoding' || job.State === 'Packaging';
                const downloadUrl = getApiUrl(`SeriesDownloader/Jobs/${encodeURIComponent(job.JobId)}/Download?token=${encodeURIComponent(job.DownloadToken || '')}`);
                const speedBadge = job.TranscodeSpeed
                    ? `<span class="jsd-badge" style="background:rgba(16,185,129,0.18); color:#34d399;">⚡ ${escapeHtml(job.TranscodeSpeed)}${job.TranscodeFps ? ` (${escapeHtml(job.TranscodeFps)} fps)` : ''}</span>`
                    : '';
                const hwBadge = job.HardwareAccelInfo
                    ? `<span class="jsd-badge" style="background:rgba(168,85,247,0.18); color:#c084fc;">${escapeHtml(job.HardwareAccelInfo)}</span>`
                    : '';

                return `
                    <div class="jsd-job-card">
                        <div style="display:flex; justify-content:space-between; align-items:flex-start; gap:10px; flex-wrap:wrap;">
                            <div>
                                <div style="font-weight:600; font-size:1rem;">${escapeHtml(job.Title)}</div>
                                <div style="font-size:0.82rem; color:#9ca3af; margin-top:2px;">
                                    ${escapeHtml(job.QualitySummary)} &bull; ${Number(job.CompletedEpisodes) || 0}/${Number(job.TotalEpisodes) || 0} episode(s)
                                    ${job.OutputFileSizeBytes ? ` &bull; ${escapeHtml(formatBytes(job.OutputFileSizeBytes))}` : ''}
                                </div>
                            </div>
                            <div style="display:flex; gap:6px; align-items:center; flex-wrap:wrap;">
                                ${hwBadge}
                                ${speedBadge}
                                <span class="jsd-badge">${escapeHtml(job.State)} (${Number(job.ProgressPercent) || 0}%)</span>
                            </div>
                        </div>
                        <div class="jsd-progress-track">
                            <div class="jsd-progress-fill" style="width:${Math.min(100, Math.max(0, Number(job.ProgressPercent) || 0))}%"></div>
                        </div>
                        <div style="display:flex; justify-content:space-between; align-items:center; font-size:0.82rem; color:#d1d5db;">
                            <span>${job.ErrorMessage ? `<span style="color:#ef4444">${escapeHtml(job.ErrorMessage)}</span>` : escapeHtml(job.CurrentEpisodeName || '')}</span>
                            <div style="display:flex; gap:8px;">
                                ${isDone ? `
                                    <a href="${escapeHtml(downloadUrl)}" download class="jsd-btn jsd-btn-success jsd-btn-sm" style="text-decoration:none;">
                                        <span class="material-icons" style="font-size:16px;">file_download</span>
                                        Save ${job.IsZipArchive ? '.ZIP' : 'Video'}
                                    </a>
                                ` : ''}
                                ${isActive ? `
                                    <button type="button" class="jsd-btn jsd-btn-danger jsd-cancel-job" data-job-id="${escapeHtml(job.JobId)}">Cancel</button>
                                ` : `
                                    <button type="button" class="jsd-btn jsd-btn-secondary jsd-btn-sm jsd-delete-job" data-job-id="${escapeHtml(job.JobId)}">Remove</button>
                                `}
                            </div>
                        </div>
                    </div>
                `;
            }).join('');

            bodyEl.querySelectorAll('.jsd-cancel-job').forEach(btn => {
                btn.addEventListener('click', async () => {
                    await apiFetch(`SeriesDownloader/Jobs/${encodeURIComponent(btn.dataset.jobId)}?remove=false`, { method: 'DELETE' });
                    renderJobsTab();
                });
            });

            bodyEl.querySelectorAll('.jsd-delete-job').forEach(btn => {
                btn.addEventListener('click', async () => {
                    await apiFetch(`SeriesDownloader/Jobs/${encodeURIComponent(btn.dataset.jobId)}?remove=true`, { method: 'DELETE' });
                    renderJobsTab();
                });
            });
        } catch (err) {
            bodyEl.innerHTML = `<div style="color:#ef4444; padding:16px;">Error loading jobs: ${escapeHtml(err.message)}</div>`;
        }
    }

    function triggerBrowserDownload(url) {
        const a = document.createElement('a');
        a.href = url;
        a.setAttribute('download', '');
        a.style.display = 'none';
        document.body.appendChild(a);
        a.click();
        setTimeout(() => a.remove(), 2000);
    }

    function startGlobalJobPolling() {
        if (pollIntervalId) return;
        pollIntervalId = setInterval(async () => {
            try {
                const jobs = await apiFetch('SeriesDownloader/Jobs');
                const activeJobs = (jobs || []).filter(j => j.State === 'Queued' || j.State === 'Transcoding' || j.State === 'Packaging');

                // Auto-trigger download for newly completed jobs
                for (const j of (jobs || [])) {
                    if (j.State === 'Completed' && !autoDownloadedJobs.has(j.JobId)) {
                        const ageMs = Date.now() - new Date(j.CompletedAtUtc || j.CreatedAtUtc).getTime();
                        if (ageMs < 15000) {
                            autoDownloadedJobs.add(j.JobId);
                            triggerBrowserDownload(getApiUrl(`SeriesDownloader/Jobs/${encodeURIComponent(j.JobId)}/Download?token=${encodeURIComponent(j.DownloadToken || '')}`));
                        }
                    }
                }

                updateFloatingPill(activeJobs);

                const jobsTabActive = document.querySelector('.jsd-tab[data-tab="jobs"].active');
                if (jobsTabActive) {
                    renderJobsTab();
                }

                if (activeJobs.length === 0) {
                    clearInterval(pollIntervalId);
                    pollIntervalId = null;
                }
            } catch (_) {
                // ignore polling errors
            }
        }, 2000);
    }

    function updateFloatingPill(activeJobs) {
        injectStyles();
        let pill = document.getElementById('jsd-floating-pill');
        if (!activeJobs || activeJobs.length === 0) {
            if (pill) pill.remove();
            return;
        }

        const primary = activeJobs[0];
        if (!pill) {
            pill = document.createElement('div');
            pill.id = 'jsd-floating-pill';
            pill.className = 'jsd-floating-pill';
            pill.addEventListener('click', () => {
                openDownloadModal(activeModalItemId || getCurrentItemIdFromUrl(), 'jobs');
            });
            document.body.appendChild(pill);
        }

        pill.innerHTML = `
            <span class="material-icons" style="color:#00a4dc; font-size:18px;">downloading</span>
            <span><strong>${escapeHtml(primary.Title)}</strong>: ${Number(primary.ProgressPercent) || 0}% (${Number(primary.CompletedEpisodes) || 0}/${Number(primary.TotalEpisodes) || 0})</span>
        `;
    }

    // Expose opener globally so the Plugin Dashboard page can also invoke the modal directly
    window.JellyfinSeriesDownloader = {
        openModal: openDownloadModal
    };

    const observer = new MutationObserver(() => {
        tryInjectDetailButton();
    });

    function init() {
        observer.observe(document.body, { childList: true, subtree: true });
        window.addEventListener('hashchange', () => setTimeout(tryInjectDetailButton, 150));
        window.addEventListener('popstate', () => setTimeout(tryInjectDetailButton, 150));
        document.addEventListener('viewshow', () => setTimeout(tryInjectDetailButton, 150));
        setTimeout(tryInjectDetailButton, 500);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
