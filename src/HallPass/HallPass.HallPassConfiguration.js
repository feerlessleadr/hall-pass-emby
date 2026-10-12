define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button'], function (BaseView, loading, toast) {
    'use strict';

    function getInfo() {
        return ApiClient.getJSON(ApiClient.getUrl('HallPass/Info'));
    }

    function probeItem(id) {
        return ApiClient.getJSON(ApiClient.getUrl('HallPass/Probe/Item', { Id: id }));
    }

    return class extends BaseView {
        constructor(view, params) {
            super(view, params);
            const resultBox = view.querySelector('#hpResult');
            view.querySelector('#hpProbe').addEventListener('click', () => {
                const id = view.querySelector('#hpItemId').value.trim();
                if (!id) {
                    toast({ type: 'error', text: 'Enter an item id.' });
                    return;
                }
                loading.show();
                probeItem(id).then((result) => {
                    loading.hide();
                    resultBox.textContent = JSON.stringify(result, null, 2);
                }).catch(() => {
                    loading.hide();
                    resultBox.textContent = '';
                    toast({ type: 'error', text: 'The probe failed (check the id).' });
                });
            });
        }

        onResume(options) {
            super.onResume(options);
            const box = this.view.querySelector('#hpInfo');
            getInfo().then((info) => {
                box.textContent = info.Name + ' ' + info.Version + ' is installed.';
            }).catch(() => {
                box.textContent = 'Could not read the plugin status.';
            });
        }
    };
});
