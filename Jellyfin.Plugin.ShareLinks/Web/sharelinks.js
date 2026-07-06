(function () {
    var pluginId = '68540b76-ee74-436d-85ff-2abc884bbea6';
    var copyLabel = 'Copy Stream URL';
    var actionLabel = 'Create guest link';
    var clientVersion = '1.0.0-ui-modal-8';
    var allowedItemStorageKey = 'sharelinks.allowedItemId';
    var guestClassName = 'sharelinks-guest';
    var hiddenAttr = 'data-sharelinks-hidden';
    var injectedAttr = 'data-sharelinks-injected';
    var configPromise = null;
    var userPromise = null;
    var guestStatePromise = null;
    var booted = false;
    var scanQueued = false;
    var bootRetry = null;
    var observer = null;
    var lastContextItemId = null;
    var lastContextItemTs = 0;
    var historyPatched = false;
    var durationOptions = [
        { label: '1 hour', hours: 1 },
        { label: '2 hours', hours: 2 },
        { label: '4 hours', hours: 4 },
        { label: '6 hours', hours: 6 },
        { label: '12 hours', hours: 12 },
        { label: '1 day', hours: 24 },
        { label: '2 days', hours: 48 },
        { label: '7 days', hours: 168 },
        { label: '30 days', hours: 720 }
    ];

    window.ShareLinksClientVersion = clientVersion;

    function ready() {
        return !!window.ApiClient && !!window.document && !!document.body;
    }

    function start() {
        if (booted) {
            return;
        }

        if (!ready()) {
            if (!bootRetry) {
                bootRetry = window.setTimeout(function () {
                    bootRetry = null;
                    start();
                }, 250);
            }
            return;
        }

        booted = true;
        installHooks();
        scheduleWork();
    }

    function installHooks() {
        if (!historyPatched) {
            historyPatched = true;
            patchHistory();
        }

        window.addEventListener('hashchange', scheduleWork, true);
        window.addEventListener('popstate', scheduleWork, true);

        document.addEventListener('pointerdown', function (event) {
            var node = event.target;
            while (node && node !== document) {
                var id = readItemIdFromNode(node);
                if (id) {
                    lastContextItemId = id;
                    lastContextItemTs = Date.now();
                    return;
                }
                node = node.parentElement;
            }
        }, true);

        observer = new MutationObserver(scheduleWork);
        observer.observe(document.body, { childList: true, subtree: true });

        window.setInterval(scheduleWork, 3000);
    }

    function patchHistory() {
        var pushState = history.pushState;
        var replaceState = history.replaceState;

        history.pushState = function () {
            var result = pushState.apply(this, arguments);
            scheduleWork();
            return result;
        };

        history.replaceState = function () {
            var result = replaceState.apply(this, arguments);
            scheduleWork();
            return result;
        };
    }

    function scheduleWork() {
        if (scanQueued || !ready()) {
            return;
        }

        scanQueued = true;
        window.requestAnimationFrame(function () {
            scanQueued = false;
            refresh().catch(function () {
                // Best effort only. The menu hook should never block the web UI.
            });
        });
    }

    async function refresh() {
        rememberAllowedItemFromRoute();
        await applyGuestLockdown();
        await scanForMoreMenuActions();
    }

    function apiGet(path) {
        return ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl(path),
            dataType: 'json'
        });
    }

    function apiPost(path, body) {
        return ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(path),
            dataType: 'json',
            contentType: 'application/json',
            data: JSON.stringify(body || {})
        });
    }

    function getConfig() {
        if (!configPromise) {
            configPromise = ApiClient.getPluginConfiguration(pluginId).catch(function () {
                return {};
            });
        }
        return configPromise;
    }

    function getCurrentUser() {
        if (!userPromise) {
            userPromise = apiGet('Users/Me').catch(function () {
                return null;
            });
        }
        return userPromise;
    }

    function getGuestState() {
        if (!guestStatePromise) {
            guestStatePromise = apiGet('ShareLinks/GuestState').catch(function () {
                return null;
            });
        }
        return guestStatePromise;
    }

    async function applyGuestLockdown() {
        var context = await getGuestContext();
        if (!context.locked) {
            return;
        }

        ensureGuestStyle();
        ensurePluginHideStyle(context.hiddenSelectors);
        hideGuestControls();

        // UX-only lockdown: the guest user's real access boundary is still the
        // server-side policy and item tags. This just keeps the web client out
        // of the user's way.
        if (context.allowedItemId && !isAllowedLocation(context.allowedItemId)) {
            navigateToItem(context.allowedItemId);
        }
    }

    async function getGuestContext() {
        var config = await getConfig();
        var user = await getCurrentUser();
        var state = await getGuestState();
        var prefix = config && config.GuestUsernamePrefix ? String(config.GuestUsernamePrefix) : 'share-';
        var username = user && user.Name ? String(user.Name) : '';
        var lockdownEnabled = config && config.GuestModeLockdownEnabled !== false;
        if (state && state.lockdownEnabled === false) {
            lockdownEnabled = false;
        }

        var locked = lockdownEnabled && (
            (username && username.indexOf(prefix) === 0)
            || !!(state && (state.IsGuest === true || state.isGuest === true || state.GuestUserId || state.guestUserId))
        );
        return {
            locked: locked,
            allowedItemId: extractAllowedItemId(state) || sessionStorage.getItem(allowedItemStorageKey) || null,
            username: username,
            prefix: prefix,
            lockdownEnabled: lockdownEnabled,
            hiddenSelectors: (state && (state.HiddenSelectors || state.hiddenSelectors)) || ''
        };
    }

    function extractAllowedItemId(state) {
        if (!state) {
            return null;
        }

        return state.AllowedItemId || state.allowedItemId || state.ItemId || state.itemId || state.ShareItemId || state.shareItemId || null;
    }

    function ensureGuestStyle() {
        if (document.body.classList.contains(guestClassName)) {
            return;
        }

        document.body.classList.add(guestClassName);
        if (document.getElementById('ShareLinksGuestStyle')) {
            return;
        }

        var style = document.createElement('style');
        style.id = 'ShareLinksGuestStyle';
        style.textContent = 'body.' + guestClassName + ' [' + hiddenAttr + '="1"],'
            + ' body.' + guestClassName + ' .headerHomeButton,'
            + ' body.' + guestClassName + ' .mainDrawerButton,'
            + ' body.' + guestClassName + ' .headerSearchButton,'
            + ' body.' + guestClassName + ' [data-action="addtoplaylist"],'
            + ' body.' + guestClassName + ' [data-action="addtocollection"] { display: none !important; }'
            + ' body.' + guestClassName + ' .card,'
            + ' body.' + guestClassName + ' #castCollapsible a,'
            + ' body.' + guestClassName + ' .detailsGroupItem a,'
            + ' body.' + guestClassName + ' .genresGroup a,'
            + ' body.' + guestClassName + ' .studiosGroup a,'
            + ' body.' + guestClassName + ' .mediaInfoItem a { pointer-events: none !important; cursor: default !important; }';
        document.head.appendChild(style);
    }

    function ensurePluginHideStyle(selectors) {
        var value = String(selectors || '').split(',').map(function (part) {
            return part.trim();
        }).filter(Boolean);
        var existing = document.getElementById('ShareLinksPluginHideStyle');
        if (value.length === 0) {
            if (existing) {
                existing.remove();
            }
            return;
        }

        var css = value.join(', ') + ' { display: none !important; }';
        if (existing) {
            if (existing.textContent !== css) {
                existing.textContent = css;
            }
            return;
        }

        var style = document.createElement('style');
        style.id = 'ShareLinksPluginHideStyle';
        style.textContent = css;
        document.head.appendChild(style);
    }

    function hideGuestControls() {
        var roots = [
            document.querySelector('.skinHeader'),
            document.querySelector('.mainDrawer'),
            document.querySelector('.mainDrawerPanel'),
            document.querySelector('.pageContainer'),
            document.body
        ].filter(Boolean);
        var keywords = ['home', 'search', 'library', 'settings', 'download', 'share'];

        roots.forEach(function (root) {
            Array.from(root.querySelectorAll('button, a, [role="button"], [role="menuitem"]')).forEach(function (node) {
                if (shouldHideNode(node, keywords)) {
                    node.setAttribute(hiddenAttr, '1');
                }
            });
        });
    }

    function shouldHideNode(node, keywords) {
        if (!node || node.getAttribute(hiddenAttr) === '1') {
            return false;
        }

        var label = getVisibleLabel(node).toLowerCase();
        if (!label) {
            return false;
        }

        return keywords.some(function (word) {
            return label.indexOf(word) >= 0;
        });
    }

    function getVisibleLabel(node) {
        return normalizeText(node.getAttribute('aria-label') || node.getAttribute('title') || node.textContent || '');
    }

    function normalizeText(value) {
        return String(value || '').replace(/\s+/g, ' ').trim();
    }

    function isItemGuid(value) {
        return /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i.test(String(value || '').trim());
    }

    async function scanForMoreMenuActions() {
        var user = await getCurrentUser();
        if (!isAdministrator(user)) {
            return;
        }

        var copyNodes = [];
        Array.from(document.querySelectorAll('button, a, [role="menuitem"], [role="option"], .actionsheetMenuItem, .paperListButton')).forEach(function (node) {
            if (isCopyStreamUrlLabel(getVisibleLabel(node))) {
                copyNodes.push(node);
                insertActionAfter(node);
            }
        });

        if (copyNodes.length === 0) {
            insertIntoOpenMenuFallback();
        }
    }

    function isCopyStreamUrlLabel(label) {
        var value = normalizeText(label).toLowerCase();
        if (!value) {
            return false;
        }

        return value === copyLabel.toLowerCase()
            || (value.indexOf('copy') >= 0 && value.indexOf('stream') >= 0 && value.indexOf('url') >= 0)
            || (value.indexOf('copier') >= 0 && value.indexOf('url') >= 0 && (value.indexOf('flux') >= 0 || value.indexOf('stream') >= 0));
    }

    function insertIntoOpenMenuFallback() {
        var itemId = resolveItemId(document);
        if (!itemId) {
            return;
        }

        var container = findOpenActionContainer();
        if (!container || container.querySelector('[' + injectedAttr + '="1"]')) {
            return;
        }

        var template = findBestActionTemplate(container);
        if (!template) {
            return;
        }

        insertActionAfter(template, itemId, true);
    }

    function findOpenActionContainer() {
        var selectors = [
            '.actionSheet',
            '.actionsheet',
            '.actionSheetContent',
            '.dialog',
            '.paperDialog',
            '.mdl-dialog',
            '.listItemBody',
            '[role="dialog"]',
            '[role="menu"]'
        ];

        for (var i = 0; i < selectors.length; i += 1) {
            var nodes = document.querySelectorAll(selectors[i]);
            for (var j = 0; j < nodes.length; j += 1) {
                var node = nodes[j];
                if (isVisible(node) && looksLikeActionContainer(node)) {
                    return node;
                }
            }
        }

        return null;
    }

    function looksLikeActionContainer(node) {
        var actions = Array.from(node.querySelectorAll('button, a, [role="menuitem"], [role="option"], .actionsheetMenuItem, .paperListButton'))
            .filter(isVisible);
        return actions.length >= 2 && actions.length <= 40;
    }

    function findBestActionTemplate(container) {
        var actions = Array.from(container.querySelectorAll('button, a, [role="menuitem"], [role="option"], .actionsheetMenuItem, .paperListButton'))
            .filter(isVisible);
        for (var i = 0; i < actions.length; i += 1) {
            if (isCopyStreamUrlLabel(getVisibleLabel(actions[i]))) {
                return actions[i];
            }
        }

        return actions.length ? actions[actions.length - 1] : null;
    }

    function isVisible(node) {
        return !!(node && (node.offsetWidth || node.offsetHeight || node.getClientRects().length));
    }

    function insertActionAfter(copyNode, explicitItemId, appendToParent) {
        var parent = copyNode.parentElement;
        if (!parent || parent.querySelector('[' + injectedAttr + '="1"]')) {
            return;
        }

        var itemId = explicitItemId || resolveItemId(copyNode) || resolveItemId(document);
        if (!itemId) {
            return;
        }

        var sourceLabel = getVisibleLabel(copyNode);
        var injected = copyNode.cloneNode(true);
        injected.setAttribute(injectedAttr, '1');
        injected.setAttribute('type', 'button');
        injected.removeAttribute('id');
        injected.removeAttribute('href');
        injected.removeAttribute('onclick');
        injected.removeAttribute('target');
        injected.removeAttribute('download');
        injected.setAttribute('aria-label', actionLabel);
        injected.setAttribute('title', actionLabel);
        injected.dataset.sharelinksItemId = itemId;
        injected.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            void createGuestLink(itemId);
        }, true);

        if (!replaceText(injected, sourceLabel, actionLabel)) {
            injected.textContent = actionLabel;
        }

        if (appendToParent) {
            parent.appendChild(injected);
        } else {
            copyNode.insertAdjacentElement('afterend', injected);
        }
    }

    function replaceText(root, from, to) {
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
        while (walker.nextNode()) {
            var node = walker.currentNode;
            if (normalizeText(node.nodeValue) === from) {
                node.nodeValue = node.nodeValue.replace(from, to);
                return true;
            }
        }
        return false;
    }

    function resolveItemId(node) {
        var direct = parseItemIdFromUrl();
        if (direct) {
            return direct;
        }

        var current = node;
        while (current && current !== document) {
            var id = readItemIdFromNode(current);
            if (id) {
                return id;
            }
            current = current.parentElement;
        }

        if (lastContextItemId && (Date.now() - lastContextItemTs) < 8000) {
            return lastContextItemId;
        }

        return findItemIdInDocument();
    }

    function parseItemIdFromUrl() {
        var sources = [location.hash, location.search, location.href];
        for (var i = 0; i < sources.length; i += 1) {
            var text = sources[i];
            var match = text.match(/[?&](?:id|itemId)=([^&#]+)/i);
            if (match && match[1]) {
                var decoded = decodeURIComponent(match[1]);
                if (isItemGuid(decoded)) {
                    return decoded;
                }
            }
        }

        return null;
    }

    function findItemIdInDocument() {
        var selectors = [
            '#itemDetailPage',
            '.detailPage',
            '.itemDetailPage',
            '[data-itemid]',
            '[data-id]'
        ];

        for (var i = 0; i < selectors.length; i += 1) {
            var nodes = document.querySelectorAll(selectors[i]);
            for (var j = 0; j < nodes.length; j += 1) {
                var id = readItemIdFromNode(nodes[j]);
                if (id) {
                    return id;
                }
            }
        }

        return null;
    }

    function readItemIdFromNode(node) {
        if (!node || !node.getAttribute) {
            return null;
        }

        var candidates = [
            node.getAttribute('data-itemid'),
            node.getAttribute('data-id'),
            node.getAttribute('data-item-id')
        ];

        for (var i = 0; i < candidates.length; i += 1) {
            if (isItemGuid(candidates[i])) {
                return candidates[i];
            }
        }

        return null;
    }

    function isAdministrator(user) {
        return !!(user && user.Policy && user.Policy.IsAdministrator === true);
    }

    function rememberAllowedItemFromRoute() {
        if (!isDetailsOrPlaybackRoute()) {
            return;
        }

        var itemId = parseItemIdFromUrl() || findItemIdInDocument();
        if (itemId) {
            sessionStorage.setItem(allowedItemStorageKey, itemId);
        }
    }

    function isDetailsOrPlaybackRoute() {
        return /#\/(?:details|video|playback|list|item)/i.test(location.hash || '');
    }

    function isAllowedLocation(allowedItemId) {
        var hash = location.hash || '';
        if (/#\/(?:video|playback)/i.test(hash)) {
            return true;
        }
        if (/#\/(?:details|item)/i.test(hash)) {
            var id = parseItemIdFromUrl();
            return !!id && !!allowedItemId && id.toLowerCase() === String(allowedItemId).toLowerCase();
        }
        return false;
    }

    function navigateToItem(itemId) {
        var target = '#/details?id=' + encodeURIComponent(itemId);
        if (location.hash !== target) {
            location.hash = target;
        }
    }

    async function createGuestLink(itemId) {
        if (!isItemGuid(itemId)) {
            notify('Could not determine which item to share. Open the item page and retry.');
            return;
        }

        try {
            var config = await getConfig();
            var user = await getCurrentUser();
            if (!isAdministrator(user)) {
                notify('ShareLinks is available to administrators only.');
                return;
            }

            if (config && config.Enabled === false) {
                notify('ShareLinks is disabled.');
                return;
            }

            var result = await chooseExpiryHours(config, function (expiryHours) {
                var payload = {
                    itemId: itemId,
                    expiryHours: expiryHours,
                    oneUse: config && config.OneUseDefault !== undefined ? !!config.OneUseDefault : true
                };

                var shareUrlPromise = apiPost('ShareLinks/Admin/Create', payload).then(function (response) {
                    var shareUrl = response && (response.ShareUrl || response.shareUrl);
                    if (!shareUrl) {
                        throw new Error('The server did not return a share URL.');
                    }

                    return shareUrl;
                });

                var copiedPromise = copyTextWhenReady(shareUrlPromise);
                return shareUrlPromise.then(function (shareUrl) {
                    return copiedPromise.catch(function () {
                        return false;
                    }).then(function (copied) {
                        return {
                            shareUrl: shareUrl,
                            copied: copied
                        };
                    });
                });
            });
            if (!result) {
                return;
            }

            showShareResult(result.shareUrl, result.copied);
        } catch (error) {
            notify(extractErrorMessage(error, 'Could not create a guest link.'));
        }
    }

    function chooseExpiryHours(config, onChoose) {
        var options = durationOptions.map(function (option) {
            return {
                label: option.label,
                hours: option.hours
            };
        });

        return openModal({
            title: 'Create guest link',
            body: 'Choose how long this link should stay valid.',
            options: options,
            onChoose: onChoose,
            cancelText: 'Cancel'
        });
    }

    function copyTextWhenReady(textPromise) {
        if (navigator.clipboard && navigator.clipboard.write && window.ClipboardItem && window.Blob) {
            try {
                var blobPromise = Promise.resolve(textPromise).then(function (text) {
                    return new Blob([text], { type: 'text/plain' });
                });
                return navigator.clipboard.write([
                    new ClipboardItem({ 'text/plain': blobPromise })
                ]).then(function () {
                    return true;
                }).catch(function () {
                    return Promise.resolve(textPromise).then(copyText);
                });
            } catch (error) {
                return Promise.resolve(textPromise).then(copyText);
            }
        }

        return Promise.resolve(textPromise).then(copyText);
    }

    function copyText(text) {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            return navigator.clipboard.writeText(text).then(function () {
                return true;
            }).catch(function () {
                return fallbackCopy(text);
            });
        }

        return Promise.resolve(fallbackCopy(text));
    }

    function notify(message) {
        ensureShareLinksUi();
        var toast = document.createElement('div');
        toast.className = 'sharelinks-toast';
        toast.textContent = message;
        document.body.appendChild(toast);
        window.setTimeout(function () {
            toast.classList.add('is-visible');
        }, 20);
        window.setTimeout(function () {
            toast.classList.remove('is-visible');
            window.setTimeout(function () {
                toast.remove();
            }, 180);
        }, 3600);
    }

    function showShareResult(shareUrl, copied) {
        ensureShareLinksUi();
        var body = document.createElement('div');
        var note = document.createElement('p');
        note.className = 'sharelinks-note';
        note.textContent = copied
            ? 'The link was copied to your clipboard.'
            : 'The link was created, but the browser blocked automatic clipboard access.';
        body.appendChild(note);

        var urlBox = document.createElement('textarea');
        urlBox.className = 'sharelinks-url';
        urlBox.readOnly = true;
        urlBox.value = shareUrl;
        body.appendChild(urlBox);

        openModalElement({
            title: copied ? 'Share link copied' : 'Share link created',
            bodyElement: body,
            actions: [
                {
                    label: 'Copy',
                    primary: true,
                    handler: function () {
                        return copyText(shareUrl).then(function (ok) {
                            if (ok) {
                                notify('Share link copied.');
                            } else {
                                urlBox.focus();
                                urlBox.select();
                                notify('Select and copy the link manually.');
                            }
                            return ok;
                        });
                    }
                },
                {
                    label: 'Done',
                    close: true
                }
            ],
            onOpen: function () {
                urlBox.focus();
                urlBox.select();
            }
        });
    }

    function openModal(settings) {
        var body = document.createElement('div');
        var text = document.createElement('p');
        text.className = 'sharelinks-note';
        text.textContent = settings.body;
        body.appendChild(text);

        var grid = document.createElement('div');
        grid.className = 'sharelinks-duration-grid';
        body.appendChild(grid);

        return new Promise(function (resolve) {
            var modal = openModalElement({
                title: settings.title,
                bodyElement: body,
                actions: [
                    {
                        label: settings.cancelText || 'Cancel',
                        close: true,
                        handler: function () {
                            resolve(null);
                        }
                    }
                ],
                onDismiss: function () {
                    resolve(null);
                }
            });

            settings.options.forEach(function (option) {
                var button = document.createElement('button');
                button.type = 'button';
                button.className = 'sharelinks-duration-button';
                button.textContent = option.label;

                button.addEventListener('click', function () {
                    modal.close();
                    if (settings.onChoose) {
                        resolve(settings.onChoose(option.hours));
                    } else {
                        resolve(option.hours);
                    }
                });
                grid.appendChild(button);
            });
        });
    }

    function openModalElement(settings) {
        ensureShareLinksUi();
        var closed = false;
        var overlay = document.createElement('div');
        overlay.className = 'sharelinks-overlay';
        overlay.setAttribute('role', 'presentation');

        var dialog = document.createElement('div');
        dialog.className = 'sharelinks-dialog';
        dialog.setAttribute('role', 'dialog');
        dialog.setAttribute('aria-modal', 'true');
        dialog.setAttribute('aria-label', settings.title);
        overlay.appendChild(dialog);

        var title = document.createElement('h3');
        title.className = 'sharelinks-title';
        title.textContent = settings.title;
        dialog.appendChild(title);

        dialog.appendChild(settings.bodyElement);

        var actions = document.createElement('div');
        actions.className = 'sharelinks-actions';
        dialog.appendChild(actions);

        function close() {
            if (closed) {
                return;
            }
            closed = true;
            overlay.classList.remove('is-visible');
            window.setTimeout(function () {
                overlay.remove();
            }, 160);
        }

        (settings.actions || []).forEach(function (action) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = action.primary ? 'sharelinks-action primary' : 'sharelinks-action';
            button.textContent = action.label;
            button.addEventListener('click', function () {
                var result = action.handler ? action.handler() : null;
                Promise.resolve(result).finally(function () {
                    if (action.close) {
                        close();
                    }
                });
            });
            actions.appendChild(button);
        });

        overlay.addEventListener('click', function (event) {
            if (event.target === overlay) {
                close();
                if (settings.onDismiss) {
                    settings.onDismiss();
                }
            }
        });
        document.addEventListener('keydown', function onKeyDown(event) {
            if (event.key === 'Escape' && !closed) {
                document.removeEventListener('keydown', onKeyDown, true);
                close();
                if (settings.onDismiss) {
                    settings.onDismiss();
                }
            }
        }, true);

        document.body.appendChild(overlay);
        window.setTimeout(function () {
            overlay.classList.add('is-visible');
            var firstButton = overlay.querySelector('button:not([disabled])');
            if (firstButton) {
                firstButton.focus();
            }
            if (settings.onOpen) {
                settings.onOpen();
            }
        }, 20);

        return { close: close, element: overlay };
    }

    function ensureShareLinksUi() {
        if (document.getElementById('ShareLinksUiStyle')) {
            return;
        }

        var style = document.createElement('style');
        style.id = 'ShareLinksUiStyle';
        style.textContent = [
            '.sharelinks-overlay{position:fixed;inset:0;z-index:999999;background:rgba(0,0,0,.58);display:grid;place-items:center;padding:24px;opacity:0;transition:opacity .16s ease;}',
            '.sharelinks-overlay.is-visible{opacity:1;}',
            '.sharelinks-dialog{width:min(520px,100%);background:var(--background-color,#202020);color:var(--text-color,#fff);box-shadow:0 18px 60px rgba(0,0,0,.45);border:1px solid rgba(255,255,255,.12);border-radius:8px;padding:22px;}',
            '.sharelinks-title{font-size:1.25rem;line-height:1.3;margin:0 0 14px;font-weight:600;}',
            '.sharelinks-note{margin:0 0 16px;color:var(--text-secondary-color,#cfcfcf);line-height:1.45;}',
            '.sharelinks-duration-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;margin-top:4px;}',
            '.sharelinks-duration-button,.sharelinks-action{border:0;border-radius:6px;background:rgba(255,255,255,.12);color:inherit;padding:10px 12px;min-height:42px;cursor:pointer;font:inherit;}',
            '.sharelinks-duration-button:hover:not(:disabled),.sharelinks-action:hover{background:rgba(255,255,255,.18);}',
            '.sharelinks-duration-button:disabled{opacity:.35;cursor:not-allowed;}',
            '.sharelinks-actions{display:flex;gap:10px;justify-content:flex-end;margin-top:18px;}',
            '.sharelinks-action.primary{background:var(--theme-primary-color,#00a4dc);color:#fff;}',
            '.sharelinks-url{width:100%;min-height:88px;box-sizing:border-box;border:1px solid rgba(255,255,255,.2);border-radius:6px;background:rgba(0,0,0,.18);color:inherit;padding:10px;font:inherit;resize:vertical;}',
            '.sharelinks-toast{position:fixed;left:24px;bottom:24px;z-index:1000000;max-width:min(460px,calc(100vw - 48px));background:rgba(24,24,24,.96);color:#fff;border:1px solid rgba(255,255,255,.14);border-radius:6px;padding:11px 14px;box-shadow:0 10px 30px rgba(0,0,0,.35);opacity:0;transform:translateY(8px);transition:opacity .18s ease,transform .18s ease;}',
            '.sharelinks-toast.is-visible{opacity:1;transform:translateY(0);}',
            '@media (max-width:520px){.sharelinks-duration-grid{grid-template-columns:repeat(2,minmax(0,1fr));}.sharelinks-dialog{padding:18px;}.sharelinks-actions{justify-content:stretch;}.sharelinks-action{flex:1;}}'
        ].join('');
        document.head.appendChild(style);
    }

    function fallbackCopy(text) {
        var textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.setAttribute('readonly', 'readonly');
        textarea.style.position = 'fixed';
        textarea.style.top = '-1000px';
        textarea.style.left = '-1000px';
        document.body.appendChild(textarea);
        textarea.focus();
        textarea.select();
        var copied = false;
        try {
            copied = document.execCommand('copy');
        } catch (error) {
            copied = false;
        }
        textarea.remove();
        return copied;
    }

    function clampPositiveInteger(value, fallback) {
        var parsed = parseInt(value, 10);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
    }

    function extractErrorMessage(error, fallback) {
        if (!error) {
            return fallback;
        }

        if (typeof error === 'string') {
            return error;
        }

        if (error.responseJSON && error.responseJSON.error) {
            return error.responseJSON.error;
        }

        if (error.responseText) {
            return error.responseText;
        }

        if (error.message) {
            return error.message;
        }

        return fallback;
    }

    start();
})();
