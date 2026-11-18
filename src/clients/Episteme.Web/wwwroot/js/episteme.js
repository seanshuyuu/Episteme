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
    downloadText(filename, content, contentType) {
        const blob = new Blob([content], { type: contentType });
        const link = document.createElement("a");
        link.href = URL.createObjectURL(blob);
        link.download = filename;
        link.click();
        URL.revokeObjectURL(link.href);
    }
};
