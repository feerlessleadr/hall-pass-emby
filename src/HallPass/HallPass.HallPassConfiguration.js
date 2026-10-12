define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button', 'emby-select', 'emby-checkbox', 'emby-textarea'], function (BaseView, loading, toast) {
    'use strict';

    const pluginId = '6ca80343-2b60-40de-ab81-7a41628fcaf2';
    const PAGE_SIZE = 50;
    const DECISIONS = [
        ['Explicit', 'Explicit', 'hp-explicit'],
        ['Review', 'Needs review', 'hp-review'],
        ['LikelyClean', 'Likely clean', 'hp-likelyclean'],
        ['Clean', 'Clean', 'hp-clean'],
        ['Skipped', 'Skipped', 'hp-skipped']
    ];
    const LABELS = { Explicit: 'Explicit', Review: 'Review', LikelyClean: 'Likely clean', Clean: 'Clean', Skipped: 'Skipped', Unscanned: 'Not scanned' };
    const REASONS = { NoEvidence: 'no evidence', Suspected: 'suspected', Conflict: 'conflict', WeakClean: 'faint sign of clean' };

    function getJson(name, params) {
        return ApiClient.getJSON(ApiClient.getUrl(name, params));
    }

    function postJson(name, data) {
        return ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl(name), data: JSON.stringify(data || {}), contentType: 'application/json', dataType: 'json' });
    }

    // Everything that comes from a file's tags goes in with textContent, never as HTML.
    function el(tag, className, text) {
        const e = document.createElement(tag);
        if (className) { e.className = className; }
        if (text !== undefined && text !== null) { e.textContent = text; }
        return e;
    }

    return class extends BaseView {
        constructor(view, params) {
            super(view, params);
            this.page = 0;
            this.total = 0;
            this.pollTimer = null;

            view.querySelector('.localnav').addEventListener('click', (e) => {
                e.preventDefault();
                const target = e.target.closest('.nav-button');
                if (!target) { return; }
                const id = target.getAttribute('data-target');
                view.querySelectorAll('.localnav .nav-button').forEach(b => b.classList.remove('ui-btn-active'));
                target.classList.add('ui-btn-active');
                view.querySelectorAll('#hpDashPage, #hpReviewPage, #hpSettingsPage').forEach(p => p.classList.toggle('hide', p.id !== id));
                if (id === 'hpReviewPage') { this.loadReview(); }
                if (id === 'hpSettingsPage') { this.loadSettings(); }
                if (id === 'hpDashPage') { this.refreshStatus(); }
            });

            view.querySelector('#hpScan').addEventListener('click', () => this.startScan(false));
            view.querySelector('#hpScanAll').addEventListener('click', () => this.startScan(true));
            view.querySelector('#hpCancel').addEventListener('click', () => {
                postJson('HallPass/Scan/Cancel').then(() => this.refreshStatus());
            });

            view.querySelector('#hpApplyFilters').addEventListener('click', () => { this.page = 0; this.loadReview(); });
            view.querySelector('#hpFText').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); this.page = 0; this.loadReview(); } });
            view.querySelector('#hpPrev').addEventListener('click', () => { if (this.page > 0) { this.page--; this.loadReview(); } });
            view.querySelector('#hpNext').addEventListener('click', () => { if ((this.page + 1) * PAGE_SIZE < this.total) { this.page++; this.loadReview(); } });
            view.querySelector('#hpSelectAll').addEventListener('change', (e) => {
                view.querySelectorAll('#hpReviewList .hp-pick').forEach(c => { c.checked = e.target.checked; });
                this.updateSelectionCount();
            });
            view.querySelector('#hpMarkExplicit').addEventListener('click', () => this.decide('explicit'));
            view.querySelector('#hpMarkClean').addEventListener('click', () => this.decide('clean'));
            view.querySelector('#hpClearManual').addEventListener('click', () => this.decide('clearOverride'));

            view.querySelector('.hpSettingsForm').addEventListener('submit', (e) => {
                e.preventDefault();
                this.saveSettings();
            });
        }

        // ----- dashboard -------------------------------------------------------------------------------------

        refreshStatus() {
            return getJson('HallPass/Status').then((s) => {
                this.renderStatus(s);
                return s;
            }).catch(() => {
                this.view.querySelector('#hpJobText').textContent = 'Could not read the status.';
            });
        }

        renderStatus(s) {
            const view = this.view;
            view.querySelector('#hpBanner').classList.toggle('hide', !s.DryRun);
            const tiles = view.querySelector('#hpTiles');
            tiles.textContent = '';
            const by = (s.Stats && s.Stats.ByDecision) || {};
            DECISIONS.forEach((d) => {
                const tile = el('div', 'hp-tile ' + d[2]);
                tile.appendChild(el('div', 'hp-count', String(by[d[0]] || 0)));
                tile.appendChild(el('div', 'hp-label', d[1]));
                tiles.appendChild(tile);
            });
            const reasons = (s.Stats && s.Stats.ByReason) || {};
            const parts = Object.keys(reasons).map(k => reasons[k] + ' ' + (REASONS[k] || k));
            view.querySelector('#hpReasons').textContent = (s.Stats ? s.Stats.Total + ' tracks known' : '') + (parts.length ? '. In review: ' + parts.join(', ') + '.' : '.')
                + (s.Stats && s.Stats.Overrides ? ' ' + s.Stats.Overrides + ' with a manual decision.' : '');

            const job = s.Job || {};
            const running = job.State === 'Running';
            view.querySelector('#hpJobText').textContent = running
                ? 'Scanning: ' + job.Processed + ' of ' + job.Total + ' tracks (' + job.Scanned + ' looked at, ' + job.Unchanged + ' unchanged' + (job.Failed ? ', ' + job.Failed + ' failed' : '') + ')'
                : (job.State === 'Idle' ? 'No scan has run since Emby started.' : job.Message);
            const bar = view.querySelector('#hpProgress');
            bar.max = Math.max(1, job.Total || 1);
            bar.value = running || job.State === 'Completed' ? (job.Processed || 0) : 0;
            view.querySelector('#hpLastScan').textContent = s.Stats && s.Stats.LastScanUtc ? 'Last track scanned: ' + new Date(s.Stats.LastScanUtc).toLocaleString() : '';
            view.querySelector('#hpScan').disabled = running;
            view.querySelector('#hpScanAll').disabled = running;
            view.querySelector('#hpCancel').classList.toggle('hide', !running);
            this.wasRunning = running;
        }

        startScan(force) {
            postJson('HallPass/Scan', { Force: force }).then(() => this.refreshStatus()).catch(() => {
                toast({ type: 'error', text: 'Could not start the scan (is one already running?).' });
                this.refreshStatus();
            });
        }

        // ----- review ----------------------------------------------------------------------------------------

        loadReview() {
            const view = this.view;
            const params = {
                Decision: view.querySelector('#hpFDecision').value,
                Reason: view.querySelector('#hpFReason').value,
                Sort: view.querySelector('#hpFSort').value,
                Text: view.querySelector('#hpFText').value.trim(),
                Overrides: view.querySelector('#hpFOverrides').checked,
                Skip: this.page * PAGE_SIZE,
                Take: PAGE_SIZE
            };
            loading.show();
            getJson('HallPass/Review', params).then((r) => {
                loading.hide();
                this.total = r.Total;
                this.renderReview(r);
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Could not load the review list.' });
            });
        }

        renderReview(r) {
            const view = this.view;
            const list = view.querySelector('#hpReviewList');
            list.textContent = '';
            view.querySelector('#hpSelectAll').checked = false;
            if (!r.Items.length) {
                list.appendChild(el('div', 'hp-note', 'Nothing matches. Run a scan on the Dashboard if you have not yet, or change the filters.'));
            } else {
                const table = el('table', 'hp-table');
                const head = el('tr');
                ['', 'Track', 'Decision', 'Why'].forEach(h => head.appendChild(el('th', null, h)));
                table.appendChild(head);
                r.Items.forEach((item) => {
                    const row = el('tr');
                    const pick = el('td');
                    const box = document.createElement('input');
                    box.type = 'checkbox';
                    box.className = 'hp-pick';
                    box.dataset.id = String(item.Id);
                    box.addEventListener('change', () => this.updateSelectionCount());
                    pick.appendChild(box);
                    row.appendChild(pick);

                    const name = el('td');
                    name.appendChild(el('div', null, item.Name));
                    name.appendChild(el('div', 'fieldDescription', [item.Artist, item.Album].filter(Boolean).join(' / ')));
                    row.appendChild(name);

                    const decision = el('td');
                    const cls = (DECISIONS.filter(d => d[0] === item.Decision)[0] || [])[2] || '';
                    decision.appendChild(el('span', 'hp-pill ' + cls, LABELS[item.Decision] || item.Decision));
                    decision.appendChild(el('div', 'fieldDescription', item.Confidence ? item.Confidence.toFixed(2) : ''));
                    if (item.ReviewReason && item.ReviewReason !== 'None') {
                        decision.appendChild(el('div', 'fieldDescription', REASONS[item.ReviewReason] || item.ReviewReason));
                    }
                    if (item.Override && item.Override !== 'None') {
                        decision.appendChild(el('span', 'hp-manual', 'Manual: ' + item.Override.toLowerCase()));
                    }
                    row.appendChild(decision);

                    const why = el('td', 'hp-why');
                    if (item.Evidence.length) {
                        item.Evidence.forEach(line => why.appendChild(el('div', null, line)));
                    } else {
                        why.appendChild(el('div', null, item.Skipped ? 'In a skipped genre.' : 'No evidence.'));
                    }
                    row.appendChild(why);
                    table.appendChild(row);
                });
                list.appendChild(table);
            }
            const from = this.total === 0 ? 0 : this.page * PAGE_SIZE + 1;
            const to = Math.min(this.total, (this.page + 1) * PAGE_SIZE);
            view.querySelector('#hpPageInfo').textContent = from + ' to ' + to + ' of ' + this.total;
            view.querySelector('#hpPrev').disabled = this.page === 0;
            view.querySelector('#hpNext').disabled = (this.page + 1) * PAGE_SIZE >= this.total;
            this.updateSelectionCount();
        }

        selectedIds() {
            return Array.from(this.view.querySelectorAll('#hpReviewList .hp-pick:checked')).map(c => Number(c.dataset.id));
        }

        updateSelectionCount() {
            const n = this.selectedIds().length;
            this.view.querySelector('#hpSelCount').textContent = n ? n + ' selected' : '';
        }

        decide(decision) {
            const ids = this.selectedIds();
            if (!ids.length) {
                toast({ type: 'error', text: 'Tick at least one track first.' });
                return;
            }
            loading.show();
            postJson('HallPass/Items/Decide', { ItemIds: ids, Decision: decision }).then((r) => {
                loading.hide();
                toast({ type: 'info', text: r.Changed + ' track(s) updated.' });
                this.loadReview();
                this.refreshStatus();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Could not save the decision.' });
            });
        }

        // ----- settings --------------------------------------------------------------------------------------

        loadSettings() {
            const view = this.view;
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then((c) => {
                loading.hide();
                this.config = c;
                view.querySelector('#hpPreset').value = c.Preset;
                view.querySelector('#hpLikelyClean').value = c.LikelyCleanConfidence;
                view.querySelector('#hpMinWords').value = c.MinWordsForClean;
                view.querySelector('#hpCap').value = c.PerTermCap;
                view.querySelector('#hpSkipGenres').value = (c.SkipGenres || []).join('\n');
                view.querySelector('#hpTagExplicit').value = c.ExplicitTag;
                view.querySelector('#hpTagClean').value = c.CleanTag;
                view.querySelector('#hpTagLikely').value = c.LikelyCleanTag;
                view.querySelector('#hpTagReview').value = c.ReviewTag;
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Could not load the settings.' });
            });
        }

        saveSettings() {
            const view = this.view;
            const c = this.config || {};
            c.Preset = view.querySelector('#hpPreset').value;
            c.LikelyCleanConfidence = parseFloat(view.querySelector('#hpLikelyClean').value);
            c.MinWordsForClean = parseInt(view.querySelector('#hpMinWords').value, 10);
            c.PerTermCap = parseInt(view.querySelector('#hpCap').value, 10);
            c.SkipGenres = view.querySelector('#hpSkipGenres').value.split('\n').map(s => s.trim()).filter(Boolean);
            c.ExplicitTag = view.querySelector('#hpTagExplicit').value;
            c.CleanTag = view.querySelector('#hpTagClean').value;
            c.LikelyCleanTag = view.querySelector('#hpTagLikely').value;
            c.ReviewTag = view.querySelector('#hpTagReview').value;
            loading.show();
            ApiClient.updatePluginConfiguration(pluginId, c).then(() => {
                loading.hide();
                toast({ type: 'info', text: 'Settings saved.' });
                this.refreshStatus();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'The settings were not saved. Check the values (tag names must differ; confidence 0.1 to 0.9).' });
            });
        }

        // ----- lifecycle -------------------------------------------------------------------------------------

        onResume(options) {
            super.onResume(options);
            this.refreshStatus();
            this.pollTimer = setInterval(() => {
                const dashVisible = !this.view.querySelector('#hpDashPage').classList.contains('hide');
                if (dashVisible || this.wasRunning) { this.refreshStatus(); }
            }, 2000);
        }

        onPause() {
            super.onPause();
            if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = null; }
        }

        destroy() {
            if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = null; }
            super.destroy();
        }
    };
});
