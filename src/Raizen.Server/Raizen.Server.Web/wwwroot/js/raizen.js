window.raizenCharts = {
    create: function (id, config) {
        const existing = Chart.getChart(id);
        if (existing) existing.destroy();
        const canvas = document.getElementById(id);
        if (!canvas) return;
        new Chart(canvas, config);
    },
    destroy: function (id) {
        const c = Chart.getChart(id);
        if (c) c.destroy();
    }
};

window.raizenSetTitle = function (title) { document.title = title; };

// ── Theme toggle ───────────────────────────────────────────────────────────
window.raizenTheme = {
    _cookieStr: function (theme) {
        var c = 'rz-theme=' + theme + ';path=/;max-age=31536000;SameSite=Strict';
        if (location.protocol === 'https:') c += ';Secure';
        return c;
    },
    _normalize: function (theme) {
        return theme === 'light' ? 'light' : 'dark';
    },
    _apply: function (theme) {
        theme = this._normalize(theme);
        if (theme === 'dark') {
            document.documentElement.setAttribute('data-theme', 'dark');
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
        document.documentElement.setAttribute('data-theme-ready', '');
        return theme;
    },
    set: function (theme) {
        theme = this._apply(theme);
        try { localStorage.setItem('rz-theme', theme); } catch (e) { }
        try { document.cookie = this._cookieStr(theme); } catch (e) { }
    },
    init: function () {
        var theme = null;
        try { theme = localStorage.getItem('rz-theme'); } catch (e) { }
        if (!theme) {
            try {
                var m = document.cookie.match(/(?:^|;\s*)rz-theme=(\w+)/);
                if (m) theme = m[1];
            } catch (e) { }
        }
        this.set(this._normalize(theme));
    },
    isDark: function () {
        return document.documentElement.getAttribute('data-theme') === 'dark';
    }
};
window.raizenTheme.init();

// ── Real-time toast notifications ─────────────────────────────────────────
window.raizenToasts = {
    _keepaliveId: null,
    _audio: null,
    _soundEnabled: true,

    initSound: function () {
        try {
            this._audio = new Audio('/sounds/notify.wav');
            this._audio.volume = 0.4;
        } catch (e) { }
        var saved = localStorage.getItem('rz-toast-sound');
        this._soundEnabled = saved !== 'false';
    },

    playSound: function () {
        if (!this._soundEnabled || !this._audio) return;
        try {
            this._audio.currentTime = 0;
            this._audio.play().catch(function() {});
        } catch (e) { }
    },

    toggleSound: function () {
        this._soundEnabled = !this._soundEnabled;
        try { localStorage.setItem('rz-toast-sound', String(this._soundEnabled)); } catch (e) { }
        return this._soundEnabled;
    },

    isSoundEnabled: function () {
        return this._soundEnabled;
    },

    requestPermission: function () {
        if ('Notification' in window && Notification.permission === 'default') {
            Notification.requestPermission();
        }
    },

    showIfHidden: function (title, body, url) {
        if (!document.hidden) return;
        // Validate URL is a safe relative path (prevent javascript:, data:, external URLs)
        var safeUrl = (typeof url === 'string' && url.startsWith('/') && !url.startsWith('//')) ? url : null;
        if ('Notification' in window && Notification.permission === 'granted') {
            var n = new Notification(title, { body: body, icon: '/images/raizen.jpg', tag: safeUrl || '' });
            n.onclick = function () {
                window.focus();
                if (safeUrl) window.location.href = safeUrl;
                n.close();
            };
        }
    },

    startKeepalive: function () {
        if (window.raizenToasts._keepaliveId) return;
        // Ping every 4 minutes to keep the SignalR circuit alive.
        // Updating the document title triggers a Blazor JS interop round-trip.
        window.raizenToasts._keepaliveId = setInterval(function () {
            try { document.title = document.title; } catch (e) { }
        }, 240000);
    },

    stopKeepalive: function () {
        if (window.raizenToasts._keepaliveId) {
            clearInterval(window.raizenToasts._keepaliveId);
            window.raizenToasts._keepaliveId = null;
        }
    }
};

window.blazorDownloadFile = (filename, contentType, base64) => {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    const blob = new Blob([bytes], { type: contentType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 5000);
};
