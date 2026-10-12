define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button', 'emby-select', 'emby-checkbox', 'emby-textarea'], function (BaseView, loading, toast) {
    'use strict';

    const pluginId = '6ca80343-2b60-40de-ab81-7a41628fcaf2';
    const PAGE_SIZE = 50;
    const PAGES = '#hpDashPage, #hpReviewPage, #hpHistoryPage, #hpSettingsPage';
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

    function tagList(tags) {
        return '[' + (tags || []).join(', ') + ']';
    }

    function when(utc) {
        return utc ? new Date(utc).toLocaleString() : '';
    }

    return class extends BaseView {
        constructor(view, params) {
            super(view, params);
            this.page = 0;
            this.total = 0;
            this.pollTimer = null;
            this.dryRun = true;
            this.lastPreview = null;

            view.querySelector('.localnav').addEventListener('click', (e) => {
                e.preventDefault();
                const target = e.target.closest('.nav-button');
                if (!target) { return; }
                const id = target.getAttribute('data-target');
                view.querySelectorAll('.localnav .nav-button').forEach(b => b.classList.remove('ui-btn-active'));
                target.classList.add('ui-btn-active');
                view.querySelectorAll(PAGES).forEach(p => p.classList.toggle('hide', p.id !== id));
                if (id === 'hpReviewPage') { this.loadReview(); }
                if (id === 'hpHistoryPage') { this.loadHistory(); }
                if (id === 'hpSettingsPage') { this.loadSettings(); }
                if (id === 'hpDashPage') { this.refreshStatus(); }
            });

            view.querySelector('#hpScan').addEventListener('click', () => this.startScan(false));
            view.querySelector('#hpScanAll').addEventListener('click', () => this.startScan(true));
            view.querySelector('#hpCancel').addEventListener('click', () => {
                postJson('HallPass/Scan/Cancel').then(() => this.refreshStatus());
            });

            view.querySelector('#hpPreviewBtn').addEventListener('click', () => this.preview());
            view.querySelector('#hpApplyBtn').addEventListener('click', () => this.apply());
            view.querySelector('#hpWriteCancel').addEventListener('click', () => {
                postJson('HallPass/Write/Cancel').then(() => this.refreshStatus());
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
            this.dryRun = !!s.DryRun;
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
            const scanning = job.State === 'Running';
            const online = job.OnlineAsked || job.OnlineCached || job.OnlineFailed
                ? ' Online: ' + job.OnlineAsked + ' asked, ' + job.OnlineCached + ' from saved answers' + (job.OnlineFailed ? ', ' + job.OnlineFailed + ' without a usable answer' : '') + '.'
                : '';
            view.querySelector('#hpJobText').textContent = (scanning
                ? 'Scanning: ' + job.Processed + ' of ' + job.Total + ' tracks (' + job.Scanned + ' looked at, ' + job.Unchanged + ' unchanged' + (job.Failed ? ', ' + job.Failed + ' failed' : '') + ').'
                : (job.State === 'Idle' ? 'No scan has run since Emby started.' : job.Message)) + online;
            const bar = view.querySelector('#hpProgress');
            bar.max = Math.max(1, job.Total || 1);
            bar.value = scanning || job.State === 'Completed' ? (job.Processed || 0) : 0;
            const auto = s.Auto || {};
            view.querySelector('#hpLastScan').textContent = (s.Stats && s.Stats.LastScanUtc ? 'Last track scanned: ' + when(s.Stats.LastScanUtc) + '.' : '')
                + (auto.Waiting ? ' ' + auto.Waiting + ' new track(s) waiting to be looked at.' : '')
                + (auto.Processed ? ' ' + auto.Processed + ' new track(s) looked at automatically' + (auto.LastUtc ? ' (last: ' + when(auto.LastUtc) + ')' : '') + '.' : '');

            const write = s.Write || {};
            const writing = write.State === 'Running';
            const busy = scanning || writing;
            view.querySelector('#hpScan').disabled = busy;
            view.querySelector('#hpScanAll').disabled = busy;
            view.querySelector('#hpCancel').classList.toggle('hide', !scanning);

            const wbar = view.querySelector('#hpWriteProgress');
            wbar.classList.toggle('hide', !(writing || write.State === 'Completed' || write.State === 'Cancelled'));
            wbar.max = Math.max(1, write.Total || 1);
            wbar.value = write.Processed || 0;
            view.querySelector('#hpWriteText').textContent = writing
                ? (write.Kind === 'undo' ? 'Putting tags back: ' : 'Writing tags: ') + write.Processed + ' of ' + write.Total + ' tracks (' + write.Changed + ' changed)'
                : (write.State && write.State !== 'Idle' ? write.Message : '');
            view.querySelector('#hpApplyBtn').disabled = busy || this.dryRun;
            view.querySelector('#hpPreviewBtn').disabled = busy;
            view.querySelector('#hpWriteCancel').classList.toggle('hide', !writing);
            view.querySelector('#hpDryRunNote').textContent = this.dryRun
                ? 'Dry run is on, so "Apply decisions" is switched off. Turn dry run off under Settings > Writing tags when you are happy with the preview.'
                : 'Dry run is off: "Apply decisions" writes tags to your library.';
            this.wasBusy = busy;
            if (this.wasWriting && !writing) { this.preview(true); }
            this.wasWriting = writing;
        }

        startScan(force) {
            postJson('HallPass/Scan', { Force: force }).then(() => this.refreshStatus()).catch(() => {
                toast({ type: 'error', text: 'Could not start the scan (is another job running?).' });
                this.refreshStatus();
            });
        }

        // ----- tags: preview and apply -----------------------------------------------------------------------

        preview(quiet) {
            const view = this.view;
            return getJson('HallPass/Apply/Preview').then((p) => {
                this.lastPreview = p;
                const byTag = Object.keys(p.ByTag || {}).map(k => p.ByTag[k] + ' to ' + k);
                view.querySelector('#hpPreviewText').textContent = p.WouldChange
                    ? p.WouldChange + ' of ' + p.Total + ' tracks would change: ' + byTag.join(', ') + '. ' + p.Unchanged + ' already have the right tag' + (p.Missing ? ', ' + p.Missing + ' are no longer in the library' : '') + '.'
                    : 'Nothing would change: all ' + p.Unchanged + ' decided tracks already have the right tag' + (p.Missing ? ' (' + p.Missing + ' are no longer in the library)' : '') + '.';
                const box = view.querySelector('#hpPreviewSample');
                box.textContent = '';
                (p.Sample || []).forEach((item) => {
                    box.appendChild(el('div', null, item.Artist + ' - ' + item.Name + ':  ' + tagList(item.Before) + '  ->  ' + tagList(item.After)));
                });
                if (p.WouldChange > (p.Sample || []).length) {
                    box.appendChild(el('div', 'fieldDescription', '... and ' + (p.WouldChange - p.Sample.length) + ' more'));
                }
            }).catch(() => {
                if (!quiet) { toast({ type: 'error', text: 'Could not work out the preview.' }); }
            });
        }

        apply() {
            this.preview(true).then(() => {
                const p = this.lastPreview;
                if (!p || !p.WouldChange) {
                    toast({ type: 'info', text: 'Nothing to change.' });
                    return;
                }
                if (!window.confirm('Write tags to ' + p.WouldChange + ' tracks in your library? Every change is journalled and can be undone from the History tab.')) {
                    return;
                }
                const large = p.WouldChange > 5000;
                if (large && !window.confirm(p.WouldChange + ' is a lot of tracks. Really write them all?')) {
                    return;
                }
                postJson('HallPass/Apply', { Confirm: true, ConfirmLarge: large }).then((r) => {
                    toast({ type: 'info', text: r.Started ? 'Writing tags...' : r.Message });
                    this.refreshStatus();
                }).catch(() => {
                    toast({ type: 'error', text: 'Could not start. Is dry run still on, or is another job running?' });
                    this.refreshStatus();
                });
            });
        }

        // ----- history ---------------------------------------------------------------------------------------

        loadHistory() {
            const view = this.view;
            getJson('HallPass/Jobs').then((jobs) => {
                const list = view.querySelector('#hpHistoryList');
                list.textContent = '';
                if (!jobs || !jobs.length) {
                    list.appendChild(el('div', 'hp-note', 'Nothing has been written yet.'));
                    return;
                }
                const table = el('table', 'hp-table hp-history');
                const head = el('tr');
                ['When', 'What', 'Result', ''].forEach(h => head.appendChild(el('th', null, h)));
                table.appendChild(head);
                jobs.forEach((job) => {
                    const row = el('tr');
                    row.appendChild(el('td', null, when(job.StartedUtc)));
                    row.appendChild(el('td', null, job.Kind === 'undo' ? 'Undo' : (job.Kind === 'auto' ? 'Automatic (new tracks)' : 'Apply decisions')));
                    const result = el('td');
                    result.appendChild(el('div', null, job.Unfinished ? 'Did not finish (Emby stopped, or it is still running)'
                        : job.Changed + ' changed, ' + job.Unchanged + ' already right' + (job.Failed ? ', ' + job.Failed + ' failed' : '') + (job.Conflicts ? ', ' + job.Conflicts + ' left alone (edited since)' : '')));
                    if (job.Cancelled) { result.appendChild(el('div', 'fieldDescription', 'Cancelled part way')); }
                    if (job.UndoneBy) { result.appendChild(el('div', 'fieldDescription', 'Undone')); }
                    row.appendChild(result);
                    const action = el('td');
                    const button = document.createElement('button');
                    button.setAttribute('is', 'emby-button');
                    button.type = 'button';
                    button.className = 'raised';
                    button.appendChild(el('span', null, 'Undo'));
                    button.addEventListener('click', () => this.undo(job));
                    action.appendChild(button);
                    row.appendChild(action);
                    table.appendChild(row);
                });
                list.appendChild(table);
            }).catch(() => toast({ type: 'error', text: 'Could not load the history.' }));
        }

        undo(job) {
            const force = this.view.querySelector('#hpForceUndo').checked;
            if (!window.confirm('Put back the tags this job changed (' + job.Changed + ' tracks)?' + (force ? ' Tracks edited since will be overwritten.' : ' Tracks edited since are left alone.'))) {
                return;
            }
            postJson('HallPass/Jobs/' + encodeURIComponent(job.JobId) + '/Undo', { Force: force }).then(() => {
                toast({ type: 'info', text: 'Putting tags back...' });
                this.refreshStatus();
                setTimeout(() => this.loadHistory(), 2500);
            }).catch(() => {
                toast({ type: 'error', text: 'Could not start the undo (is another job running?).' });
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
                view.querySelector('#hpDryRun').checked = !!c.DryRun;
                view.querySelector('#hpSavers').checked = !!c.RunMetadataSavers;
                view.querySelector('#hpSkippedClean').checked = !!c.TagSkippedAsClean;
                view.querySelector('#hpBatch').value = c.BatchSize;
                view.querySelector('#hpReadTags').checked = !!c.ReadFileTags;
                view.querySelector('#hpAutoScan').checked = !!c.AutoScanNewTracks;
                view.querySelector('#hpApplyAfter').checked = !!c.ApplyAfterScan;
                view.querySelector('#hpOnline').checked = !!c.OnlineEnabled;
                view.querySelector('#hpDeezer').checked = !!c.OnlineDeezer;
                view.querySelector('#hpITunes').checked = !!c.OnlineITunes;
                view.querySelector('#hpCacheDays').value = c.OnlineCacheDays;
                view.querySelector('#hpNegDays').value = c.OnlineNegativeDays;
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Could not load the settings.' });
            });
        }

        saveSettings() {
            const view = this.view;
            const c = this.config || {};
            const dryRun = view.querySelector('#hpDryRun').checked;
            if (c.DryRun && !dryRun && !window.confirm('Turning dry run off lets Hall Pass change the tags of your tracks when you press "Apply decisions". Nothing is written until you do, and every change can be undone. Continue?')) {
                view.querySelector('#hpDryRun').checked = true;
                return;
            }
            c.Preset = view.querySelector('#hpPreset').value;
            c.LikelyCleanConfidence = parseFloat(view.querySelector('#hpLikelyClean').value);
            c.MinWordsForClean = parseInt(view.querySelector('#hpMinWords').value, 10);
            c.PerTermCap = parseInt(view.querySelector('#hpCap').value, 10);
            c.SkipGenres = view.querySelector('#hpSkipGenres').value.split('\n').map(s => s.trim()).filter(Boolean);
            c.ExplicitTag = view.querySelector('#hpTagExplicit').value;
            c.CleanTag = view.querySelector('#hpTagClean').value;
            c.LikelyCleanTag = view.querySelector('#hpTagLikely').value;
            c.ReviewTag = view.querySelector('#hpTagReview').value;
            c.DryRun = dryRun;
            c.RunMetadataSavers = view.querySelector('#hpSavers').checked;
            c.TagSkippedAsClean = view.querySelector('#hpSkippedClean').checked;
            c.BatchSize = parseInt(view.querySelector('#hpBatch').value, 10);
            c.ReadFileTags = view.querySelector('#hpReadTags').checked;
            c.AutoScanNewTracks = view.querySelector('#hpAutoScan').checked;
            const applyAfter = view.querySelector('#hpApplyAfter').checked;
            if (!c.ApplyAfterScan && applyAfter && !dryRun && !window.confirm('Write tags automatically after the nightly scan and the scan of new tracks? Every change is journalled and can be undone from History.')) {
                view.querySelector('#hpApplyAfter').checked = false;
                return;
            }
            c.ApplyAfterScan = applyAfter;
            const wasOnline = !!c.OnlineEnabled;
            c.OnlineEnabled = view.querySelector('#hpOnline').checked;
            c.OnlineDeezer = view.querySelector('#hpDeezer').checked;
            c.OnlineITunes = view.querySelector('#hpITunes').checked;
            c.OnlineCacheDays = parseInt(view.querySelector('#hpCacheDays').value, 10);
            c.OnlineNegativeDays = parseInt(view.querySelector('#hpNegDays').value, 10);
            if (!wasOnline && c.OnlineEnabled && !window.confirm('Turn on online lookups? Hall Pass will ask Deezer and iTunes about your tracks, sending only the artist, title, ISRC or album barcode (never file names, paths or lyrics). You can turn this off again at any time.')) {
                view.querySelector('#hpOnline').checked = false;
                c.OnlineEnabled = false;
                return;
            }
            loading.show();
            ApiClient.updatePluginConfiguration(pluginId, c).then(() => {
                loading.hide();
                toast({ type: 'info', text: 'Settings saved.' });
                this.refreshStatus();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'The settings were not saved. Check the values (tag names must differ; confidence 0.1 to 0.9; batch size 1 to 2000).' });
            });
        }

        // ----- lifecycle -------------------------------------------------------------------------------------

        onResume(options) {
            super.onResume(options);
            this.refreshStatus();
            this.pollTimer = setInterval(() => {
                const dashVisible = !this.view.querySelector('#hpDashPage').classList.contains('hide');
                if (dashVisible || this.wasBusy) { this.refreshStatus(); }
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
