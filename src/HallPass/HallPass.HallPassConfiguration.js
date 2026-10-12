define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button', 'emby-select', 'emby-checkbox', 'emby-textarea'], function (BaseView, loading, toast) {
    'use strict';

    const pluginId = '6ca80343-2b60-40de-ab81-7a41628fcaf2';
    const PAGE_SIZE = 50;
    const PAGES = '#hpDashPage, #hpReviewPage, #hpHistoryPage, #hpTryPage, #hpSettingsPage';
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
            this.scope = null;
            this.allMatching = false;
            this.lastFilters = {};
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
            view.querySelector('#hpFView').addEventListener('change', () => { this.page = 0; this.loadReview(); });
            view.querySelector('#hpScopeClear').addEventListener('click', () => { this.scope = null; this.page = 0; this.loadReview(); });
            view.querySelector('#hpSelectMatching').addEventListener('click', (e) => {
                e.preventDefault();
                this.allMatching = !this.allMatching;
                this.updateSelectionCount();
            });
            view.querySelector('#hpMarkExplicit').addEventListener('click', () => this.decide('explicit'));
            view.querySelector('#hpMarkClean').addEventListener('click', () => this.decide('clean'));
            view.querySelector('#hpWTryBtn').addEventListener('click', () => this.tryWords());
            view.querySelector('#hpTryTrack').addEventListener('click', () => this.tryTrack());
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
                    ? p.WouldChange + ' items would change (' + p.Total + ' tracks looked at)' + (p.RollUps ? ' (' + p.RollUps + ' of them albums or artists)' : '') + ': ' + byTag.join(', ') + '. ' + p.Unchanged + ' already have the right tag' + (p.Missing ? ', ' + p.Missing + ' are no longer in the library' : '') + '.'
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
                if (!window.confirm('Write tags to ' + p.WouldChange + ' items (tracks, and albums or artists if the roll-up is on) in your library? Every change is journalled and can be undone from the History tab.')) {
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
            const filters = {
                Decision: view.querySelector('#hpFDecision').value,
                Reason: view.querySelector('#hpFReason').value,
                Text: view.querySelector('#hpFText').value.trim(),
                Overrides: view.querySelector('#hpFOverrides').checked
            };
            this.lastFilters = filters;
            this.allMatching = false;
            const albums = view.querySelector('#hpFView').value === 'albums' && !this.scope;
            const scopeBox = view.querySelector('#hpScope');
            scopeBox.classList.toggle('hide', !this.scope);
            if (this.scope) {
                view.querySelector('#hpScopeText').textContent = 'Showing only ' + (this.scope.Artist || 'unknown artist') + ' / ' + (this.scope.NoAlbum ? 'tracks without an album' : this.scope.Album) + '. ';
            }
            view.querySelector('.hp-bulk').classList.toggle('hide', albums);
            if (albums) {
                this.loadAlbums(filters);
                return;
            }
            const params = Object.assign({}, filters, {
                Sort: view.querySelector('#hpFSort').value,
                Skip: this.page * PAGE_SIZE,
                Take: PAGE_SIZE
            }, this.scope || {});
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

        loadAlbums(filters) {
            loading.show();
            getJson('HallPass/Review/Albums', Object.assign({}, filters, { Skip: this.page * PAGE_SIZE, Take: PAGE_SIZE })).then((r) => {
                loading.hide();
                this.total = r.Total;
                this.renderAlbums(r);
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Could not load the albums.' });
            });
        }

        renderAlbums(r) {
            const view = this.view;
            const list = view.querySelector('#hpReviewList');
            list.textContent = '';
            if (!r.Items.length) {
                list.appendChild(el('div', 'hp-note', 'Nothing matches. Run a scan on the Dashboard if you have not yet, or change the filters.'));
            } else {
                const table = el('table', 'hp-table');
                const head = el('tr');
                ['Album', 'Tracks', 'Decisions', ''].forEach(h => head.appendChild(el('th', null, h)));
                table.appendChild(head);
                r.Items.forEach((g) => {
                    const row = el('tr');
                    const name = el('td');
                    name.appendChild(el('div', null, g.Album || '(no album)'));
                    name.appendChild(el('div', 'fieldDescription', g.Artist || '(unknown artist)'));
                    row.appendChild(name);
                    row.appendChild(el('td', null, g.Matching === g.Total ? String(g.Total) : g.Matching + ' of ' + g.Total));
                    const dec = el('td');
                    Object.keys(g.ByDecision).forEach((k) => dec.appendChild(el('div', 'fieldDescription', (LABELS[k] || k) + ': ' + g.ByDecision[k])));
                    row.appendChild(dec);
                    const act = el('td');
                    const scope = { Artist: g.Artist, Album: g.Album, NoAlbum: !g.Album };
                    const mk = (text, fn) => {
                        const b = el('button', 'raised emby-button', text);
                        b.setAttribute('is', 'emby-button');
                        b.type = 'button';
                        b.addEventListener('click', fn);
                        act.appendChild(b);
                    };
                    mk('Open', () => { this.scope = scope; this.page = 0; this.view.querySelector('#hpFView').value = 'tracks'; this.loadReview(); });
                    mk('Mark all explicit', () => this.decideAlbum('explicit', g, scope));
                    mk('Mark all clean', () => this.decideAlbum('clean', g, scope));
                    row.appendChild(act);
                    table.appendChild(row);
                });
                list.appendChild(table);
            }
            const from = this.total === 0 ? 0 : this.page * PAGE_SIZE + 1;
            const to = Math.min(this.total, (this.page + 1) * PAGE_SIZE);
            view.querySelector('#hpPageInfo').textContent = from + ' to ' + to + ' of ' + this.total + ' albums';
            view.querySelector('#hpPrev').disabled = this.page === 0;
            view.querySelector('#hpNext').disabled = (this.page + 1) * PAGE_SIZE >= this.total;
        }

        matchFields(filters, scope) {
            return {
                AllMatching: true,
                MatchDecision: filters.Decision,
                MatchReason: filters.Reason,
                MatchText: filters.Text,
                MatchOverrides: filters.Overrides,
                MatchArtist: scope ? scope.Artist : '',
                MatchAlbum: scope ? scope.Album : '',
                MatchNoAlbum: scope ? !!scope.NoAlbum : false
            };
        }

        decideAlbum(decision, g, scope) {
            const what = decision === 'explicit' ? 'explicit' : 'clean';
            if (!window.confirm('Mark ' + g.Matching + ' track(s) of "' + (g.Album || 'no album') + '" by ' + (g.Artist || 'unknown artist') + ' as ' + what + '? Nothing is written to your files until you press Apply, and you can clear it afterwards.')) {
                return;
            }
            this.sendDecision(Object.assign({ Decision: decision }, this.matchFields(this.lastFilters, scope)));
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
                    const again = el('button', 'raised emby-button', 'Rescan this track');
                    again.setAttribute('is', 'emby-button');
                    again.type = 'button';
                    again.addEventListener('click', () => this.rescanOne(item.Id));
                    why.appendChild(again);
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
            const link = this.view.querySelector('#hpSelectMatching');
            const pageFull = n > 0 && n === this.view.querySelectorAll('#hpReviewList .hp-pick').length;
            if (!pageFull) {
                this.allMatching = false;
            }
            const more = this.total > n;
            link.classList.toggle('hide', !(pageFull && more));
            link.textContent = this.allMatching ? 'Only the ' + n + ' on this page' : 'Select all ' + this.total + ' matching';
            this.view.querySelector('#hpSelCount').textContent = this.allMatching ? 'All ' + this.total + ' matching selected' : (n ? n + ' selected' : '');
        }

        decide(decision) {
            const ids = this.selectedIds();
            if (!ids.length) {
                toast({ type: 'error', text: 'Tick at least one track first.' });
                return;
            }
            if (this.allMatching) {
                const what = decision === 'clearOverride' ? 'clear the manual decision on' : 'mark as ' + decision;
                if (!window.confirm('This will ' + what + ' ALL ' + this.total + ' tracks that match the filters, across every page. Continue?')) {
                    return;
                }
                this.sendDecision(Object.assign({ Decision: decision }, this.matchFields(this.lastFilters, this.scope)));
                return;
            }
            this.sendDecision({ ItemIds: ids, Decision: decision });
        }

        sendDecision(body) {
            loading.show();
            postJson('HallPass/Items/Decide', body).then((r) => {
                loading.hide();
                toast({ type: 'info', text: r.Changed + ' track(s) updated.' + (r.Truncated ? ' Only the first ' + r.Matched + ' matched; run it again for the rest.' : '') });
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
                const lines = (arr) => (arr || []).join('\n');
                view.querySelector('#hpWSevere').value = lines(c.ExtraSevereWords);
                view.querySelector('#hpWModerate').value = lines(c.ExtraModerateWords);
                view.querySelector('#hpWMild').value = lines(c.ExtraMildWords);
                view.querySelector('#hpWIgnore').value = lines(c.IgnoredWords);
                view.querySelector('#hpWAllow').value = lines(c.ExtraAllowedWords);
                this.loadBuiltinWords();
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
                view.querySelector('#hpLrclib').checked = !!c.OnlineLyrics;
                view.querySelector('#hpRollAlbums').checked = !!c.RollUpAlbums;
                view.querySelector('#hpRollArtists').checked = !!c.RollUpArtists;
                view.querySelector('#hpCacheDays').value = c.OnlineCacheDays;
                view.querySelector('#hpNegDays').value = c.OnlineNegativeDays;
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Could not load the settings.' });
            });
        }

        tryTrack() {
            const view = this.view;
            const val = (id) => view.querySelector(id).value.trim();
            const out = view.querySelector('#hpTryOut');
            if (!val('#hpTTitle') && !val('#hpTIsrc') && !val('#hpTUpc')) {
                out.textContent = 'Give at least a title, an ISRC or an album barcode.';
                return;
            }
            out.textContent = 'Asking the online sources (this can take a few seconds)...';
            postJson('HallPass/Lookup', {
                Artist: val('#hpTArtist'),
                Title: val('#hpTTitle'),
                Album: val('#hpTAlbum'),
                Seconds: parseFloat(val('#hpTSeconds')) || 0,
                Isrc: val('#hpTIsrc'),
                Upc: val('#hpTUpc')
            }).then((r) => {
                out.textContent = '';
                out.appendChild(el('div', null, 'Would be: ' + (LABELS[r.Decision] || r.Decision) + (r.Confidence ? ' (' + r.Confidence.toFixed(2) + ')' : '') + '. ' + (r.Reason || '')));
                (r.Evidence || []).forEach((e) => out.appendChild(el('div', 'fieldDescription', e.Source + ': ' + e.Verdict + ' ' + e.Confidence.toFixed(2) + (e.Detail ? ' (' + e.Detail + ')' : ''))));
                if (r.Note) {
                    out.appendChild(el('div', 'fieldDescription', r.Note));
                }
            }).catch(() => { out.textContent = 'The lookup failed. Check your internet connection and try again.'; });
        }

        rescanOne(id) {
            loading.show();
            postJson('HallPass/Items/Rescan', { ItemId: id }).then(() => {
                loading.hide();
                toast({ type: 'info', text: 'Looked at the track again.' });
                this.loadReview();
                this.refreshStatus();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Could not rescan (is a scan or apply running?).' });
            });
        }

        loadBuiltinWords() {
            const box = this.view.querySelector('#hpWBuiltin');
            getJson('HallPass/WordList').then((w) => {
                box.textContent = '';
                [['Severe', w.Severe], ['Moderate', w.Moderate], ['Mild (counts nothing)', w.Mild], ['Also caught inside longer words', w.Stems], ['Always fine', w.Allow]].forEach((t) => {
                    box.appendChild(el('div', null, t[0] + ': ' + t[1].join(', ')));
                });
            }).catch(() => { box.textContent = 'Could not load the built-in list.'; });
        }

        tryWords() {
            const view = this.view;
            const text = view.querySelector('#hpWTryText').value;
            const out = view.querySelector('#hpWTryResult');
            if (!text.trim()) {
                out.textContent = 'Paste some text first.';
                return;
            }
            const words = (id) => view.querySelector(id).value.split('\n').map(s => s.trim()).filter(Boolean);
            postJson('HallPass/WordList/Test', {
                Text: text,
                ExtraSevereWords: words('#hpWSevere'),
                ExtraModerateWords: words('#hpWModerate'),
                ExtraMildWords: words('#hpWMild'),
                IgnoredWords: words('#hpWIgnore'),
                ExtraAllowedWords: words('#hpWAllow')
            }).then((r) => {
                const hits = r.Hits.map(h => h.Term + (h.Count > 1 ? ' x' + h.Count : '') + ' (' + h.Weight + ')').join(', ');
                out.textContent = 'Score ' + r.Score + ' over ' + r.WordCount + ' words. ' + (LABELS[r.Verdict] || r.Verdict) + ' at ' + r.Confidence.toFixed(2) + (hits ? '. Found: ' + hits : '. No listed words.');
            }).catch(() => { out.textContent = 'Could not score the text.'; });
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
            const words = (id) => view.querySelector(id).value.split('\n').map(s => s.trim()).filter(Boolean);
            c.ExtraSevereWords = words('#hpWSevere');
            c.ExtraModerateWords = words('#hpWModerate');
            c.ExtraMildWords = words('#hpWMild');
            c.IgnoredWords = words('#hpWIgnore');
            c.ExtraAllowedWords = words('#hpWAllow');
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
            c.OnlineLyrics = view.querySelector('#hpLrclib').checked;
            c.RollUpAlbums = view.querySelector('#hpRollAlbums').checked;
            c.RollUpArtists = view.querySelector('#hpRollArtists').checked;
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
