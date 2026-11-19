window.episteme = {
    loadTheme() {
        const serialized = localStorage.getItem("episteme-theme");
        return serialized ? JSON.parse(serialized) : null;
    },
    saveTheme(preferences) {
        localStorage.setItem("episteme-theme", JSON.stringify(preferences));
        const root = document.documentElement;
        const mode = preferences.mode === "system"
            ? (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light")
            : preferences.mode;
        root.dataset.theme = mode || "light";
        root.dataset.density = preferences.density || "comfortable";
        root.style.setProperty("--accent", preferences.accent || "#635bdb");
        root.style.setProperty("--accent-soft",
            `color-mix(in srgb, ${preferences.accent || "#635bdb"} 10%, var(--surface))`);
    },
    getRelativePaths(input) {
        return Array.from(input.files, file => file.webkitRelativePath);
    },
    downloadText(filename, content, contentType) {
        const blob = new Blob([content], { type: contentType });
        const link = document.createElement("a");
        link.href = URL.createObjectURL(blob);
        link.download = filename;
        link.click();
        URL.revokeObjectURL(link.href);
    },
    downloadBytes(filename, content, contentType) {
        const blob = new Blob([content], { type: contentType });
        const link = document.createElement("a");
        link.href = URL.createObjectURL(blob);
        link.download = filename;
        link.click();
        setTimeout(() => URL.revokeObjectURL(link.href), 1000);
    }
};

(() => {
    let drag = null;

    function resize(grip, width) {
        const layout = grip.closest(".workspace-layout");
        const panel = grip.dataset.resizePanel;
        const stackedHistory = window.matchMedia("(max-width: 1000px)").matches;
        const maximum = Math.max(200, Math.min(600,
            (layout.clientWidth - 360) / (stackedHistory ? 1 : 2)));
        const next = Math.round(Math.max(200, Math.min(maximum, width)));
        layout.style.setProperty(`--${panel}-width`, `${next}px`);
        grip.setAttribute("aria-valuemax", Math.floor(maximum));
        grip.setAttribute("aria-valuenow", next);
    }

    document.addEventListener("pointerdown", event => {
        const grip = event.target.closest(".panel-grip[data-resize-panel]");
        if (!grip || event.button !== 0) return;
        const layout = grip.closest(".workspace-layout");
        const panel = layout.querySelector(grip.dataset.resizePanel === "tree" ? ".tree" : ".history-panel");
        drag = { grip, pointerId: event.pointerId, startX: event.clientX, width: panel.getBoundingClientRect().width };
        grip.setPointerCapture(event.pointerId);
        event.preventDefault();
    });

    document.addEventListener("pointermove", event => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        const direction = drag.grip.dataset.resizePanel === "tree" ? 1 : -1;
        resize(drag.grip, drag.width + direction * (event.clientX - drag.startX));
    });

    document.addEventListener("pointerup", event => {
        if (drag && event.pointerId === drag.pointerId) drag = null;
    });
    document.addEventListener("pointercancel", event => {
        if (drag && event.pointerId === drag.pointerId) drag = null;
    });
    document.addEventListener("lostpointercapture", event => {
        if (drag && event.pointerId === drag.pointerId) drag = null;
    });
    document.addEventListener("keydown", event => {
        const grip = event.target.closest(".panel-grip[data-resize-panel]");
        if (!grip || !["ArrowLeft", "ArrowRight"].includes(event.key)) return;
        const layout = grip.closest(".workspace-layout");
        const panel = layout.querySelector(grip.dataset.resizePanel === "tree" ? ".tree" : ".history-panel");
        const direction = grip.dataset.resizePanel === "tree" ? 1 : -1;
        resize(grip, panel.getBoundingClientRect().width + direction * (event.key === "ArrowRight" ? 10 : -10));
        event.preventDefault();
    });
})();
