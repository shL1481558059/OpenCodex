import { ref, computed } from "vue";

const THEME_STORAGE_KEY = "opencodex-theme";

export const themeSetting = ref("system"); // 'light' | 'dark' | 'system'
export const isDark = ref(false);

function getSystemTheme() {
  return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function applyThemeToHtml(dark) {
  const el = document.documentElement;
  if (dark) {
    el.classList.add("dark");
  } else {
    el.classList.remove("dark");
  }
}

export function syncTheme() {
  const effectiveTheme = themeSetting.value === "system" ? getSystemTheme() : themeSetting.value;
  isDark.value = effectiveTheme === "dark";
  applyThemeToHtml(isDark.value);
}

export function setTheme(newTheme) {
  if (!["light", "dark", "system"].includes(newTheme)) return;
  themeSetting.value = newTheme;
  try {
    localStorage.setItem(THEME_STORAGE_KEY, newTheme);
  } catch {
    // Ignore localStorage access errors
  }
  syncTheme();
}

export function initTheme() {
  try {
    const saved = localStorage.getItem(THEME_STORAGE_KEY);
    if (saved && ["light", "dark", "system"].includes(saved)) {
      themeSetting.value = saved;
    }
  } catch {
    // Ignore localStorage access errors
  }

  syncTheme();

  if (window.matchMedia) {
    const mediaQuery = window.matchMedia("(prefers-color-scheme: dark)");
    const handleChange = () => {
      if (themeSetting.value === "system") {
        syncTheme();
      }
    };
    if (mediaQuery.addEventListener) {
      mediaQuery.addEventListener("change", handleChange);
    } else if (mediaQuery.addListener) {
      mediaQuery.addListener(handleChange);
    }
  }
}
