/*!
 * FootLook Connect - drop this into your own frontend (React, Angular, plain JS,
 * anything) to make its outgoing API calls taggable by FootLook's "Connect to site"
 * dashboard feature.
 *
 * What it does:
 *   1. On load, reads footlookSessionId/footlookTabId from the current URL's query
 *      string (present only when the tab was opened via "Connect to site").
 *   2. Persists them in sessionStorage so they survive client-side route changes
 *      and reloads within the same tab, without needing to keep the query params
 *      in the URL.
 *   3. Patches fetch() and XMLHttpRequest so every request your app makes to your
 *      configured API carries them as X-Footlook-Session-Id / X-Footlook-Tab-Id
 *      headers - the same headers FootLook's server-side capture already reads.
 *
 * This never runs unless the page was actually opened with those query params (or
 * a prior call in the same tab already stored them) - if you load this script
 * outside of a FootLook "Connect to site" session, init() is a no-op.
 *
 * Usage:
 *   <script src="https://your-api-host/footlook-connect.js"></script>
 *   <script>
 *     FootLookConnect.init({ apiBaseUrls: ['https://your-api-host'] });
 *   </script>
 *
 * apiBaseUrls scopes which outgoing requests get tagged - only calls to your own
 * API should carry these headers, not calls to Google Fonts, analytics, etc. Omit
 * it (or pass an empty array) to tag every request instead.
 *
 * If your frontend and API are on different origins, your API's CORS policy must:
 *   - allow the X-Footlook-Session-Id and X-Footlook-Tab-Id request headers (add
 *     them to Access-Control-Allow-Headers), and
 *   - allow credentials (AllowCredentials(), with a specific allowed origin, not
 *     AllowAnyOrigin - the two are mutually exclusive by spec), since tagged
 *     requests are sent with credentials so FootLook's footlook_scope_id cookie
 *     travels with them; without it, the capture would still get tagged with the
 *     right session/tab headers but would land in a scope the dashboard can never
 *     see, and would never show up filtered.
 * This cookie-sharing only works when frontend and API are same-site (same domain,
 * different port, or a shared parent domain) - a genuinely cross-site pair (e.g.
 * unrelated domains) additionally needs the API's scope cookie issued with
 * SameSite=None; Secure.
 */
(function (global) {
    'use strict';

    var STORAGE_KEY_SESSION = 'footlook_connect_session_id';
    var STORAGE_KEY_TAB = 'footlook_connect_tab_id';
    var HEADER_SESSION = 'X-Footlook-Session-Id';
    var HEADER_TAB = 'X-Footlook-Tab-Id';

    function readParam(name) {
        try {
            return new URLSearchParams(global.location.search).get(name);
        } catch {
            return null;
        }
    }

    function readStorage(key) {
        try {
            return global.sessionStorage.getItem(key);
        } catch {
            return null;
        }
    }

    function writeStorage(key, value) {
        try {
            global.sessionStorage.setItem(key, value);
        } catch {
            /* sessionStorage unavailable (private mode, etc.) - tagging still works
               for this page load via the in-memory sessionId/tabId, just won't
               survive a reload. */
        }
    }

    function normalizeBaseUrls(apiBaseUrls) {
        if (!Array.isArray(apiBaseUrls)) return [];
        return apiBaseUrls
            .map(function (u) { return String(u || '').replace(/\/+$/, ''); })
            .filter(Boolean);
    }

    function init(options) {
        options = options || {};
        var apiBaseUrls = normalizeBaseUrls(options.apiBaseUrls);

        var sessionId = readParam('footlookSessionId') || readStorage(STORAGE_KEY_SESSION);
        var tabId = readParam('footlookTabId') || readStorage(STORAGE_KEY_TAB);

        if (!sessionId || !tabId) {
            return { active: false };
        }

        writeStorage(STORAGE_KEY_SESSION, sessionId);
        writeStorage(STORAGE_KEY_TAB, tabId);

        function shouldTag(url) {
            if (!apiBaseUrls.length) return true;

            var absolute;
            try {
                absolute = new URL(url, global.location.href).href;
            } catch {
                return false;
            }

            for (var i = 0; i < apiBaseUrls.length; i++) {
                if (absolute.indexOf(apiBaseUrls[i]) === 0) return true;
            }
            return false;
        }

        var originalFetch = global.fetch;
        if (typeof originalFetch === 'function') {
            global.fetch = function (input, requestInit) {
                var url = typeof input === 'string' ? input : (input && input.url);

                if (url && shouldTag(url)) {
                    requestInit = requestInit || {};
                    var headers = new Headers(requestInit.headers || (input && input.headers) || {});
                    headers.set(HEADER_SESSION, sessionId);
                    headers.set(HEADER_TAB, tabId);
                    // FootLook's dashboard scopes captures by its own footlook_scope_id
                    // cookie. Without 'include', a cross-origin fetch (your frontend's own
                    // origin vs. your API's) never sends that cookie, so the request lands
                    // in a scope the dashboard can never query back - the tagging headers
                    // above would be set correctly but the capture would still be
                    // invisible. This only carries the cookie when the API is same-site
                    // (e.g. same domain, different port, or subdomains sharing a cookie
                    // domain) - a genuinely cross-site API/frontend pair needs the API's
                    // scope cookie issued with SameSite=None; Secure to work at all.
                    requestInit = Object.assign({}, requestInit, { headers: headers, credentials: 'include' });
                }

                return originalFetch.call(this, input, requestInit);
            };
        }

        if (typeof global.XMLHttpRequest === 'function') {
            var originalOpen = global.XMLHttpRequest.prototype.open;
            var originalSend = global.XMLHttpRequest.prototype.send;

            global.XMLHttpRequest.prototype.open = function (method, url) {
                this.__footlookUrl = url;
                return originalOpen.apply(this, arguments);
            };

            global.XMLHttpRequest.prototype.send = function () {
                if (this.__footlookUrl && shouldTag(this.__footlookUrl)) {
                    try {
                        this.setRequestHeader(HEADER_SESSION, sessionId);
                        this.setRequestHeader(HEADER_TAB, tabId);
                        this.withCredentials = true;
                    } catch {
                        /* header already sent or request already opened in a state that
                           disallows setRequestHeader - nothing to do. */
                    }
                }
                return originalSend.apply(this, arguments);
            };
        }

        return { active: true, sessionId: sessionId, tabId: tabId };
    }

    global.FootLookConnect = { init: init };
})(window);
