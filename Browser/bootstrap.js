/*
 * Arr Calendar: loads the Calendar page into Jellyfin Web.
 *
 * Registered through the JavaScript Injector plugin, which runs it once a user is signed in.
 * It only adds one script tag: the page itself is served by this plugin, versioned, so an
 * upgrade replaces it without re-registering anything here.
 *
 * Written in ES5 on purpose. JavaScript Injector serves every plugin's script from one file, so
 * syntax an older TV browser cannot parse would break the other scripts too.
 */
(function () {
    'use strict';

    if (window.__arrCalendarLoader) {
        return;
    }

    window.__arrCalendarLoader = true;

    var VERSION = '__ARR_CALENDAR_VERSION__';
    var attempts = 0;

    function load() {
        var client = window.ApiClient;
        if (!client || typeof client.getUrl !== 'function') {
            // JavaScript Injector waits for sign-in, so ApiClient is normally ready already.
            if (++attempts < 40) {
                setTimeout(load, 250);
            }

            return;
        }

        var script = document.createElement('script');
        script.src = client.getUrl('Plugins/ArrCalendar/Client/arr-calendar.js', { v: VERSION });
        script.async = true;
        script.onerror = function () {
            // The plugin was removed or switched off while this loader stayed registered.
            console.warn('Arr Calendar: the Calendar page could not be loaded.');
        };
        document.head.appendChild(script);
    }

    load();
})();
