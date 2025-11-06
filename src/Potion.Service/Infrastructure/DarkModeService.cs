using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Potion.Service.Infrastructure;

/// <summary>
/// ダークモードの実装サービス
/// テーマ切り替え機能の追加
/// </summary>
public interface IDarkModeService
{
    string GenerateDarkModeStyles();
    string GenerateDarkModeToggle();
    string GenerateThemeMetaTags();
    string ApplyDarkModeToContent(string content, ThemePreference preference);
    ThemePreference DetectUserPreference(HttpRequest request);
    string GetThemeFromCookie(HttpRequest request);
    void SetThemeCookie(HttpResponse response, ThemePreference theme);
}

/// <summary>
/// テーマ設定
/// </summary>
public enum ThemePreference
{
    Light,
    Dark,
    System,
    Auto
}

/// <summary>
/// ダークモードサービス実装
/// </summary>
public class DarkModeService : IDarkModeService
{
    public string GenerateDarkModeStyles()
    {
        return @"/* Dark Mode CSS Variables and Styles */
:root {
    /* Light theme colors (default) */
    --bg-primary: #ffffff;
    --bg-secondary: #f8f9fa;
    --bg-tertiary: #e9ecef;
    --text-primary: #212529;
    --text-secondary: #6c757d;
    --text-muted: #868e96;
    --border-color: #dee2e6;
    --shadow-color: rgba(0, 0, 0, 0.1);
    --accent-color: #007bff;
    --success-color: #28a745;
    --warning-color: #ffc107;
    --error-color: #dc3545;
    --info-color: #17a2b8;

    /* Component specific colors */
    --card-bg: #ffffff;
    --card-border: #dee2e6;
    --card-shadow: rgba(0, 0, 0, 0.1);
    --button-bg: #007bff;
    --button-text: #ffffff;
    --button-border: #007bff;
    --input-bg: #ffffff;
    --input-border: #ced4da;
    --input-focus-border: #80bdff;
    --navbar-bg: #ffffff;
    --navbar-border: #dee2e6;
    --modal-bg: #ffffff;
    --modal-overlay: rgba(0, 0, 0, 0.5);

    /* Dark theme colors */
    --dark-bg-primary: #1a1a1a;
    --dark-bg-secondary: #2d2d2d;
    --dark-bg-tertiary: #404040;
    --dark-text-primary: #ffffff;
    --dark-text-secondary: #b3b3b3;
    --dark-text-muted: #808080;
    --dark-border-color: #404040;
    --dark-shadow-color: rgba(0, 0, 0, 0.3);
    --dark-accent-color: #4dabf7;
    --dark-success-color: #51cf66;
    --dark-warning-color: #ffd43b;
    --dark-error-color: #ff6b6b;
    --dark-info-color: #339af0;

    /* Dark theme component colors */
    --dark-card-bg: #2d2d2d;
    --dark-card-border: #404040;
    --dark-card-shadow: rgba(0, 0, 0, 0.3);
    --dark-button-bg: #4dabf7;
    --dark-button-text: #ffffff;
    --dark-button-border: #4dabf7;
    --dark-input-bg: #2d2d2d;
    --dark-input-border: #404040;
    --dark-input-focus-border: #4dabf7;
    --dark-navbar-bg: #1a1a1a;
    --dark-navbar-border: #404040;
    --dark-modal-bg: #2d2d2d;
    --dark-modal-overlay: rgba(0, 0, 0, 0.8);
}

/* Base dark mode styles */
@media (prefers-color-scheme: dark) {
    :root:not([data-theme=""light""]) {
        --bg-primary: var(--dark-bg-primary);
        --bg-secondary: var(--dark-bg-secondary);
        --bg-tertiary: var(--dark-bg-tertiary);
        --text-primary: var(--dark-text-primary);
        --text-secondary: var(--dark-text-secondary);
        --text-muted: var(--dark-text-muted);
        --border-color: var(--dark-border-color);
        --shadow-color: var(--dark-shadow-color);
        --accent-color: var(--dark-accent-color);
        --success-color: var(--dark-success-color);
        --warning-color: var(--dark-warning-color);
        --error-color: var(--dark-error-color);
        --info-color: var(--dark-info-color);

        --card-bg: var(--dark-card-bg);
        --card-border: var(--dark-card-border);
        --card-shadow: var(--dark-card-shadow);
        --button-bg: var(--dark-button-bg);
        --button-text: var(--dark-button-text);
        --button-border: var(--dark-button-border);
        --input-bg: var(--dark-input-bg);
        --input-border: var(--dark-input-border);
        --input-focus-border: var(--dark-input-focus-border);
        --navbar-bg: var(--dark-navbar-bg);
        --navbar-border: var(--dark-navbar-border);
        --modal-bg: var(--dark-modal-bg);
        --modal-overlay: var(--dark-modal-overlay);
    }
}

/* Explicit dark theme */
[data-theme=""dark""] {
    --bg-primary: var(--dark-bg-primary);
    --bg-secondary: var(--dark-bg-secondary);
    --bg-tertiary: var(--dark-bg-tertiary);
    --text-primary: var(--dark-text-primary);
    --text-secondary: var(--dark-text-secondary);
    --text-muted: var(--dark-text-muted);
    --border-color: var(--dark-border-color);
    --shadow-color: var(--dark-shadow-color);
    --accent-color: var(--dark-accent-color);
    --success-color: var(--dark-success-color);
    --warning-color: var(--dark-warning-color);
    --error-color: var(--dark-error-color);
    --info-color: var(--dark-info-color);

    --card-bg: var(--dark-card-bg);
    --card-border: var(--dark-card-border);
    --card-shadow: var(--dark-card-shadow);
    --button-bg: var(--dark-button-bg);
    --button-text: var(--dark-button-text);
    --button-border: var(--dark-button-border);
    --input-bg: var(--dark-input-bg);
    --input-border: var(--dark-input-border);
    --input-focus-border: var(--dark-input-focus-border);
    --navbar-bg: var(--dark-navbar-bg);
    --navbar-border: var(--dark-navbar-border);
    --modal-bg: var(--dark-modal-bg);
    --modal-overlay: var(--dark-modal-overlay);
}

/* Dark mode body styles */
body[data-theme=""dark""],
body:not([data-theme=""light""]):not([data-theme=""auto""]) {
    background-color: var(--bg-primary);
    color: var(--text-primary);
    transition: background-color 0.3s ease, color 0.3s ease;
}

/* Dark mode card styles */
.card[data-theme=""dark""],
.card:not([data-theme=""light""]) {
    background-color: var(--card-bg);
    border-color: var(--card-border);
    box-shadow: 0 2px 8px var(--card-shadow);
}

/* Dark mode button styles */
button[data-theme=""dark""],
.btn[data-theme=""dark""],
button:not([data-theme=""light""]),
.btn:not([data-theme=""light""]) {
    background-color: var(--button-bg);
    color: var(--button-text);
    border-color: var(--button-border);
}

button[data-theme=""dark""]:hover,
.btn[data-theme=""dark""]:hover,
button:not([data-theme=""light""]):hover,
.btn:not([data-theme=""light""]):hover {
    filter: brightness(1.1);
}

/* Dark mode form styles */
input[data-theme=""dark""],
textarea[data-theme=""dark""],
select[data-theme=""dark""],
input:not([data-theme=""light""]),
textarea:not([data-theme=""light""]),
select:not([data-theme=""light""]) {
    background-color: var(--input-bg);
    color: var(--text-primary);
    border-color: var(--input-border);
}

input[data-theme=""dark""]:focus,
textarea[data-theme=""dark""]:focus,
select[data-theme=""dark""]:focus,
input:not([data-theme=""light""]):focus,
textarea:not([data-theme=""light""]):focus,
select:not([data-theme=""light""]):focus {
    border-color: var(--input-focus-border);
    box-shadow: 0 0 0 0.2rem rgba(77, 171, 247, 0.25);
}

/* Dark mode navigation */
nav[data-theme=""dark""],
nav:not([data-theme=""light""]) {
    background-color: var(--navbar-bg);
    border-color: var(--navbar-border);
}

/* Dark mode modal */
.modal[data-theme=""dark""] .modal-content,
.modal:not([data-theme=""light""]) .modal-content {
    background-color: var(--modal-bg);
    color: var(--text-primary);
}

.modal[data-theme=""dark""] .modal-overlay,
.modal:not([data-theme=""light""]) .modal-overlay {
    background-color: var(--modal-overlay);
}

/* Dark mode status indicators */
.status-success[data-theme=""dark""],
.status-success:not([data-theme=""light""]) {
    background-color: var(--dark-success-color);
    color: var(--dark-bg-primary);
}

.status-warning[data-theme=""dark""],
.status-warning:not([data-theme=""light""]) {
    background-color: var(--dark-warning-color);
    color: var(--dark-bg-primary);
}

.status-error[data-theme=""dark""],
.status-error:not([data-theme=""light""]) {
    background-color: var(--dark-error-color);
    color: var(--dark-bg-primary);
}

.status-info[data-theme=""dark""],
.status-info:not([data-theme=""light""]) {
    background-color: var(--dark-info-color);
    color: var(--dark-bg-primary);
}

/* Dark mode loading states */
.loading[data-theme=""dark""],
.loading:not([data-theme=""light""]) {
    background-color: var(--bg-secondary);
    color: var(--text-secondary);
}

.spinner[data-theme=""dark""],
.spinner:not([data-theme=""light""]) {
    border-color: var(--border-color);
    border-top-color: var(--accent-color);
}

/* Dark mode code blocks */
code[data-theme=""dark""],
pre[data-theme=""dark""],
.code-block[data-theme=""dark""],
code:not([data-theme=""light""]),
pre:not([data-theme=""light""]),
.code-block:not([data-theme=""light""]) {
    background-color: var(--bg-secondary);
    color: var(--text-primary);
    border-color: var(--border-color);
}

/* Dark mode tables */
table[data-theme=""dark""],
table:not([data-theme=""light""]) {
    background-color: var(--card-bg);
    color: var(--text-primary);
    border-color: var(--border-color);
}

table[data-theme=""dark""] th,
table[data-theme=""dark""] td,
table:not([data-theme=""light""]) th,
table:not([data-theme=""light""]) td {
    border-color: var(--border-color);
}

table[data-theme=""dark""] tbody tr:nth-child(even),
table:not([data-theme=""light""]) tbody tr:nth-child(even) {
    background-color: var(--bg-secondary);
}

table[data-theme=""dark""] tbody tr:hover,
table:not([data-theme=""light""]) tbody tr:hover {
    background-color: var(--bg-tertiary);
}

/* Dark mode scrollbar (WebKit) */
::-webkit-scrollbar {
    width: 8px;
    height: 8px;
}

::-webkit-scrollbar-track {
    background: var(--bg-secondary);
}

::-webkit-scrollbar-thumb {
    background: var(--border-color);
    border-radius: 4px;
}

::-webkit-scrollbar-thumb:hover {
    background: var(--text-muted);
}

/* Dark mode selection */
::selection {
    background-color: var(--accent-color);
    color: var(--bg-primary);
}

/* Smooth theme transitions */
* {
    transition: background-color 0.3s ease, color 0.3s ease, border-color 0.3s ease;
}

/* High contrast dark mode support */
@media (prefers-contrast: high) {
    [data-theme=""dark""] {
        --border-color: #666666;
        --dark-border-color: #666666;
    }
}

/* Reduced motion support for dark mode */
@media (prefers-reduced-motion: reduce) {
    [data-theme=""dark""] * {
        transition-duration: 0.01ms !important;
    }
}

/* Print styles for dark mode */
@media print {
    [data-theme=""dark""] {
        --bg-primary: #ffffff;
        --text-primary: #000000;
        --card-bg: #ffffff;
        --card-border: #cccccc;
    }
}

/* Focus management for dark mode */
[data-theme=""dark""] *:focus,
*:focus:not([data-theme=""light""]) {
    outline: 2px solid var(--accent-color);
    outline-offset: 2px;
}

/* Dark mode specific component overrides */
[data-theme=""dark""] .navbar-brand,
.navbar-brand:not([data-theme=""light""]) {
    color: var(--text-primary) !important;
}

[data-theme=""dark""] .navbar-nav .nav-link,
.navbar-nav .nav-link:not([data-theme=""light""]) {
    color: var(--text-primary) !important;
}

[data-theme=""dark""] .navbar-nav .nav-link:hover,
.navbar-nav .nav-link:hover:not([data-theme=""light""]) {
    color: var(--accent-color) !important;
}

/* Dark mode dropdown menus */
[data-theme=""dark""] .dropdown-menu,
.dropdown-menu:not([data-theme=""light""]) {
    background-color: var(--card-bg);
    border-color: var(--card-border);
    box-shadow: 0 4px 12px var(--shadow-color);
}

[data-theme=""dark""] .dropdown-item,
.dropdown-item:not([data-theme=""light""]) {
    color: var(--text-primary);
}

[data-theme=""dark""] .dropdown-item:hover,
.dropdown-item:hover:not([data-theme=""light""]) {
    background-color: var(--bg-secondary);
    color: var(--accent-color);
}

/* Dark mode alerts */
[data-theme=""dark""] .alert-success,
.alert-success:not([data-theme=""light""]) {
    background-color: rgba(81, 207, 102, 0.1);
    border-color: var(--success-color);
    color: var(--success-color);
}

[data-theme=""dark""] .alert-warning,
.alert-warning:not([data-theme=""light""]) {
    background-color: rgba(255, 212, 59, 0.1);
    border-color: var(--warning-color);
    color: var(--warning-color);
}

[data-theme=""dark""] .alert-error,
.alert-error:not([data-theme=""light""]) {
    background-color: rgba(255, 107, 107, 0.1);
    border-color: var(--error-color);
    color: var(--error-color);
}

[data-theme=""dark""] .alert-info,
.alert-info:not([data-theme=""light""]) {
    background-color: rgba(51, 154, 240, 0.1);
    border-color: var(--info-color);
    color: var(--info-color);
}

/* Dark mode specific utility classes */
.theme-dark {
    --bg-primary: var(--dark-bg-primary) !important;
    --text-primary: var(--dark-text-primary) !important;
}

.theme-light {
    --bg-primary: var(--bg-primary) !important;
    --text-primary: var(--text-primary) !important;
}

/* Dark mode animations */
@keyframes fadeInDark {
    from {
        background-color: var(--bg-secondary);
        opacity: 0;
    }
    to {
        background-color: var(--bg-primary);
        opacity: 1;
    }
}

[data-theme=""dark""] .fade-in,
.fade-in:not([data-theme=""light""]) {
    animation: fadeInDark 0.3s ease-in-out;
}

/* Dark mode gradients */
[data-theme=""dark""] .gradient-bg,
.gradient-bg:not([data-theme=""light""]) {
    background: linear-gradient(135deg, var(--bg-secondary) 0%, var(--bg-primary) 100%);
}

/* Dark mode specific responsive adjustments */
@media (prefers-color-scheme: dark) {
    @media (max-width: 767px) {
        /* Mobile dark mode adjustments */
        body {
            background-color: var(--dark-bg-primary);
        }

        .card {
            background-color: var(--dark-card-bg);
            border-color: var(--dark-card-border);
        }
    }
}

/* Dark mode system UI integration */
@media (prefers-color-scheme: dark) {
    /* macOS dark mode integration */
    @supports (-webkit-appearance: none) {
        body {
            background-color: var(--dark-bg-primary);
        }
    }
}

/* Windows high contrast dark mode */
@media (prefers-contrast: high) and (prefers-color-scheme: dark) {
    :root {
        --border-color: #ffffff;
        --dark-border-color: #ffffff;
        --text-primary: #ffffff;
        --dark-text-primary: #ffffff;
    }
}";
    }

    public string GenerateDarkModeToggle()
    {
        return @"<!-- Dark Mode Toggle Component -->
<div class=""theme-toggle-container"">
    <button
        id=""theme-toggle""
        class=""theme-toggle-btn""
        aria-label=""Toggle dark mode""
        aria-pressed=""false""
        onclick=""toggleTheme()""
    >
        <span class=""theme-toggle-icon light-icon"" aria-hidden=""true"">
            <!-- Sun icon for light mode -->
            <svg width=""20"" height=""20"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"">
                <circle cx=""12"" cy=""12"" r=""5""></circle>
                <line x1=""12"" y1=""1"" x2=""12"" y2=""3""></line>
                <line x1=""12"" y1=""21"" x2=""12"" y2=""23""></line>
                <line x1=""4.22"" y1=""4.22"" x2=""5.64"" y2=""5.64""></line>
                <line x1=""18.36"" y1=""18.36"" x2=""19.78"" y2=""19.78""></line>
                <line x1=""1"" y1=""12"" x2=""3"" y2=""12""></line>
                <line x1=""21"" y1=""12"" x2=""23"" y2=""12""></line>
                <line x1=""4.22"" y1=""19.78"" x2=""5.64"" y2=""18.36""></line>
                <line x1=""18.36"" y1=""5.64"" x2=""19.78"" y2=""4.22""></line>
            </svg>
        </span>

        <span class=""theme-toggle-icon dark-icon"" aria-hidden=""true"">
            <!-- Moon icon for dark mode -->
            <svg width=""20"" height=""20"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"">
                <path d=""M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z""></path>
            </svg>
        </span>

        <span class=""theme-toggle-track""></span>
        <span class=""theme-toggle-thumb""></span>
    </button>

    <!-- Theme selector dropdown -->
    <div class=""theme-selector"" id=""theme-selector"" aria-hidden=""true"">
        <div class=""theme-option"" data-theme=""light"" onclick=""setTheme('light')"">
            <span class=""theme-icon"">☀️</span>
            <span class=""theme-name"">Light</span>
        </div>
        <div class=""theme-option"" data-theme=""dark"" onclick=""setTheme('dark')"">
            <span class=""theme-icon"">🌙</span>
            <span class=""theme-name"">Dark</span>
        </div>
        <div class=""theme-option"" data-theme=""auto"" onclick=""setTheme('auto')"">
            <span class=""theme-icon"">🖥️</span>
            <span class=""theme-name"">System</span>
        </div>
    </div>
</div>

<!-- Theme Toggle JavaScript -->
<script>
// Theme management utilities
class ThemeManager {
    constructor() {
        this.currentTheme = this.getStoredTheme() || 'auto';
        this.init();
    }

    init() {
        this.applyTheme(this.currentTheme);
        this.setupEventListeners();
        this.watchSystemTheme();
    }

    getStoredTheme() {
        return localStorage.getItem('theme') || 'auto';
    }

    setStoredTheme(theme) {
        localStorage.setItem('theme', theme);
        this.currentTheme = theme;
    }

    getSystemTheme() {
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    applyTheme(theme) {
        const root = document.documentElement;

        switch (theme) {
            case 'dark':
                root.setAttribute('data-theme', 'dark');
                this.updateToggleButton(true);
                break;
            case 'light':
                root.setAttribute('data-theme', 'light');
                this.updateToggleButton(false);
                break;
            case 'auto':
            case 'system':
                root.removeAttribute('data-theme');
                const systemTheme = this.getSystemTheme();
                if (systemTheme === 'dark') {
                    root.setAttribute('data-theme', 'dark');
                }
                this.updateToggleButton(systemTheme === 'dark');
                break;
        }

        // Dispatch custom event for theme change
        window.dispatchEvent(new CustomEvent('themeChanged', {
            detail: { theme }
        }));
    }

    updateToggleButton(isDark) {
        const toggle = document.getElementById('theme-toggle');
        if (toggle) {
            toggle.setAttribute('aria-pressed', isDark.toString());

            // Update visual state
            const track = toggle.querySelector('.theme-toggle-track');
            const thumb = toggle.querySelector('.theme-toggle-thumb');

            if (track && thumb) {
                if (isDark) {
                    track.style.backgroundColor = 'var(--accent-color)';
                    thumb.style.transform = 'translateX(100%)';
                } else {
                    track.style.backgroundColor = 'var(--border-color)';
                    thumb.style.transform = 'translateX(0%)';
                }
            }
        }
    }

    setupEventListeners() {
        // Toggle button click
        const toggle = document.getElementById('theme-toggle');
        if (toggle) {
            toggle.addEventListener('click', () => {
                this.toggleTheme();
            });
        }

        // Keyboard navigation for toggle
        toggle?.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                this.toggleTheme();
            }
        });

        // Theme selector dropdown
        const selector = document.getElementById('theme-selector');
        const themeOptions = selector?.querySelectorAll('.theme-option');

        themeOptions?.forEach(option => {
            option.addEventListener('click', () => {
                const theme = option.dataset.theme;
                this.setTheme(theme);
                this.hideThemeSelector();
            });
        });

        // Click outside to close selector
        document.addEventListener('click', (e) => {
            if (!e.target.closest('.theme-toggle-container')) {
                this.hideThemeSelector();
            }
        });
    }

    watchSystemTheme() {
        const mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
        mediaQuery.addEventListener('change', (e) => {
            if (this.currentTheme === 'auto' || this.currentTheme === 'system') {
                this.applyTheme('auto');
            }
        });
    }

    toggleTheme() {
        const currentTheme = this.currentTheme;
        let newTheme;

        switch (currentTheme) {
            case 'light':
                newTheme = 'dark';
                break;
            case 'dark':
                newTheme = 'auto';
                break;
            case 'auto':
            default:
                newTheme = 'light';
                break;
        }

        this.setTheme(newTheme);
    }

    setTheme(theme) {
        this.setStoredTheme(theme);
        this.applyTheme(theme);

        // Update theme selector UI
        this.updateThemeSelector(theme);
    }

    updateThemeSelector(activeTheme) {
        const selector = document.getElementById('theme-selector');
        const options = selector?.querySelectorAll('.theme-option');

        options?.forEach(option => {
            const theme = option.dataset.theme;
            if (theme === activeTheme) {
                option.classList.add('active');
                option.setAttribute('aria-selected', 'true');
            } else {
                option.classList.remove('active');
                option.setAttribute('aria-selected', 'false');
            }
        });
    }

    showThemeSelector() {
        const selector = document.getElementById('theme-selector');
        if (selector) {
            selector.setAttribute('aria-hidden', 'false');
            selector.style.display = 'block';

            // Focus first option for keyboard navigation
            const firstOption = selector.querySelector('.theme-option');
            firstOption?.focus();
        }
    }

    hideThemeSelector() {
        const selector = document.getElementById('theme-selector');
        if (selector) {
            selector.setAttribute('aria-hidden', 'true');
            selector.style.display = 'none';
        }
    }
}

// Initialize theme manager
const themeManager = new ThemeManager();

// Global functions for backward compatibility
function toggleTheme() {
    themeManager.toggleTheme();
}

function setTheme(theme) {
    themeManager.setTheme(theme);
}

// Theme change event listener
window.addEventListener('themeChanged', (e) => {
    console.log('Theme changed to:', e.detail.theme);

    // Update any theme-dependent components
    if (typeof updateChartsForTheme === 'function') {
        updateChartsForTheme(e.detail.theme);
    }

    if (typeof updateCodeBlocksForTheme === 'function') {
        updateCodeBlocksForTheme(e.detail.theme);
    }
});

// Keyboard shortcut for theme toggle (Ctrl/Cmd + Shift + L)
document.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'L') {
        e.preventDefault();
        themeManager.toggleTheme();
    }
});
</script>

<!-- Theme Toggle CSS -->
<style>
/* Theme toggle button styles */
.theme-toggle-container {
    position: relative;
    display: inline-block;
}

.theme-toggle-btn {
    position: relative;
    width: 56px;
    height: 28px;
    background: transparent;
    border: 2px solid var(--border-color);
    border-radius: 28px;
    cursor: pointer;
    padding: 0;
    overflow: hidden;
    transition: all 0.3s ease;
}

.theme-toggle-btn:hover {
    border-color: var(--accent-color);
}

.theme-toggle-btn:focus {
    outline: 2px solid var(--accent-color);
    outline-offset: 2px;
}

.theme-toggle-track {
    position: absolute;
    top: 2px;
    left: 2px;
    width: 48px;
    height: 20px;
    background-color: var(--border-color);
    border-radius: 20px;
    transition: background-color 0.3s ease;
}

.theme-toggle-thumb {
    position: absolute;
    top: 2px;
    left: 2px;
    width: 20px;
    height: 20px;
    background-color: #fff;
    border-radius: 50%;
    box-shadow: 0 2px 4px rgba(0, 0, 0, 0.2);
    transition: transform 0.3s ease;
    z-index: 2;
}

.theme-toggle-icon {
    position: absolute;
    top: 50%;
    transform: translateY(-50%);
    z-index: 1;
    opacity: 0.7;
    transition: opacity 0.3s ease;
}

.light-icon {
    left: 4px;
    color: var(--warning-color);
}

.dark-icon {
    right: 4px;
    color: var(--accent-color);
}

/* Theme selector dropdown */
.theme-selector {
    position: absolute;
    top: 100%;
    right: 0;
    background: var(--card-bg);
    border: 1px solid var(--card-border);
    border-radius: 8px;
    box-shadow: 0 4px 12px var(--shadow-color);
    min-width: 150px;
    z-index: 1000;
    display: none;
    margin-top: 8px;
}

.theme-option {
    display: flex;
    align-items: center;
    padding: 12px 16px;
    cursor: pointer;
    transition: background-color 0.2s ease;
    border-bottom: 1px solid var(--card-border);
}

.theme-option:last-child {
    border-bottom: none;
}

.theme-option:hover,
.theme-option.active {
    background-color: var(--bg-secondary);
}

.theme-option[aria-selected=""true""] {
    background-color: var(--accent-color);
    color: var(--button-text);
}

.theme-icon {
    margin-right: 8px;
    font-size: 16px;
}

.theme-name {
    font-size: 14px;
    font-weight: 500;
}

/* Responsive adjustments */
@media (max-width: 767px) {
    .theme-toggle-btn {
        width: 48px;
        height: 24px;
    }

    .theme-toggle-track {
        width: 40px;
        height: 16px;
    }

    .theme-toggle-thumb {
        width: 16px;
        height: 16px;
    }

    .theme-toggle-icon {
        font-size: 12px;
    }

    .light-icon {
        left: 2px;
    }

    .dark-icon {
        right: 2px;
    }
}

/* High contrast mode support */
@media (prefers-contrast: high) {
    .theme-toggle-btn {
        border-width: 3px;
    }

    .theme-toggle-thumb {
        border: 2px solid var(--text-primary);
    }
}

/* Reduced motion support */
@media (prefers-reduced-motion: reduce) {
    .theme-toggle-btn,
    .theme-toggle-track,
    .theme-toggle-thumb,
    .theme-toggle-icon,
    .theme-option {
        transition: none !important;
    }
}
</style>";
    }

    public string GenerateThemeMetaTags()
    {
        return @"<!-- Dark Mode and Theme Meta Tags -->
<meta name=""theme-color"" content=""#ffffff"" media=""(prefers-color-scheme: light)"">
<meta name=""theme-color"" content=""#1a1a1a"" media=""(prefers-color-scheme: dark)"">
<meta name=""color-scheme"" content=""light dark"">
<meta name=""supported-color-schemes"" content=""light dark"">

<!-- iOS theme support -->
<meta name=""apple-mobile-web-app-status-bar-style"" content=""default"">
<meta name=""apple-mobile-web-app-status-bar-style"" content=""black-translucent"" media=""(prefers-color-scheme: dark)"">

<!-- Windows theme support -->
<meta name=""msapplication-TileColor"" content=""#ffffff"" media=""(prefers-color-scheme: light)"">
<meta name=""msapplication-TileColor"" content=""#1a1a1a"" media=""(prefers-color-scheme: dark)"">

<!-- Theme transition optimization -->
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<style>
/* Optimize theme transitions */
html {
    color-scheme: light dark;
}

@media (prefers-reduced-motion: no-preference) {
    html {
        transition: color-scheme 0.3s ease;
    }
}

/* Prevent flash of unstyled content during theme load */
html.theme-loading * {
    transition: none !important;
}
</style>

<!-- Theme preload hints -->
<link rel=""preload"" href=""css/themes.css"" as=""style"" onload=""this.onload=null;this.rel='stylesheet'"">
<noscript><link rel=""stylesheet"" href=""css/themes.css""></noscript>";
    }

    public string ApplyDarkModeToContent(string content, ThemePreference preference)
    {
        var modified = content;

        try
        {
            switch (preference)
            {
                case ThemePreference.Dark:
                    // 明示的にダークモードを適用
                    if (!modified.Contains("data-theme=\"dark\""))
                    {
                        modified = modified.Replace("<html", "<html data-theme=\"dark\"");
                    }
                    break;

                case ThemePreference.Light:
                    // 明示的にライトモードを適用
                    if (!modified.Contains("data-theme=\"light\""))
                    {
                        modified = modified.Replace("<html", "<html data-theme=\"light\"");
                    }
                    break;

                case ThemePreference.System:
                case ThemePreference.Auto:
                    // システム設定に従う（data-theme属性を削除）
                    modified = System.Text.RegularExpressions.Regex.Replace(modified,
                        @"data-theme=""(?:light|dark)""", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    break;
            }

            // テーマ関連のメタタグを追加
            var themeMetaTags = GenerateThemeMetaTags();
            if (!modified.Contains("theme-color"))
            {
                modified = modified.Replace("<head>", $"<head>{themeMetaTags}");
            }

            return modified;
        }
        catch (Exception ex)
        {
            // エラーが発生した場合は元のコンテンツを返す
            return content;
        }
    }

    public ThemePreference DetectUserPreference(HttpRequest request)
    {
        // クエリパラメータからテーマ設定を確認
        if (request.Query.TryGetValue("theme", out var themeValue))
        {
            return Enum.TryParse<ThemePreference>(themeValue, true, out var theme) ? theme : ThemePreference.System;
        }

        // ヘッダーからテーマ設定を確認（将来の拡張用）
        if (request.Headers.TryGetValue("Prefer-Theme", out var preferTheme))
        {
            return Enum.TryParse<ThemePreference>(preferTheme, true, out var theme) ? theme : ThemePreference.System;
        }

        // システム設定をデフォルトとする
        return ThemePreference.System;
    }

    public string GetThemeFromCookie(HttpRequest request)
    {
        return request.Cookies["theme"] ?? "auto";
    }

    public void SetThemeCookie(HttpResponse response, ThemePreference theme)
    {
        var cookieOptions = new CookieOptions
        {
            HttpOnly = false, // JavaScriptからアクセス可能
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTime.UtcNow.AddYears(1),
            Path = "/"
        };

        response.Cookies.Append("theme", theme.ToString().ToLowerInvariant(), cookieOptions);
    }
}
