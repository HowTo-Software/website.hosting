(() => {
    'use strict';
    let timer;
    const zones = new Set(['America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles']);
    function update() {
        document.querySelectorAll('[data-trial-countdown]').forEach(node => {
            const end = Date.parse(node.dataset.expiresUtc);
            if (!Number.isFinite(end)) return;
            const remaining = Math.max(0, Math.floor((end - Date.now()) / 1000));
            const hours = String(Math.floor(remaining / 3600)).padStart(2, '0');
            const minutes = String(Math.floor(remaining % 3600 / 60)).padStart(2, '0');
            const seconds = String(remaining % 60).padStart(2, '0');
            node.textContent = remaining > 0 ? `${hours}:${minutes}:${seconds}` : node.dataset.expiredText;
        });
        if (!document.querySelector('[data-trial-countdown]')) { clearInterval(timer); timer = undefined; }
    }
    function start() {
        clearInterval(timer); timer = undefined;
        update();
        if (!document.hidden && document.querySelector('[data-trial-countdown]')) timer = setInterval(update, 1000);
    }
    document.addEventListener('change', event => {
        if (!event.target.matches('[data-trial-timezone]') || !zones.has(event.target.value)) return;
        document.querySelectorAll('[data-trial-local-time]').forEach(node => {
            const value = Date.parse(node.dateTime);
            if (!Number.isFinite(value)) return;
            node.textContent = new Intl.DateTimeFormat(document.documentElement.lang || 'en', {
                timeZone: event.target.value, dateStyle: 'medium', timeStyle: 'long'
            }).format(new Date(value));
        });
    });
    document.addEventListener('visibilitychange', start);
    document.addEventListener('enhancedload', start);
    if (window.Blazor?.addEventListener) window.Blazor.addEventListener('enhancedload', start);
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();
