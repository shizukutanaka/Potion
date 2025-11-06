using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Potion.Service.Infrastructure;

/// <summary>
/// モバイル対応の強化サービス
/// レスポンシブデザインの最適化を実装
/// </summary>
public interface IMobileOptimizationService
{
    string OptimizeHtmlForMobile(string htmlContent);
    string OptimizeCssForMobile(string cssContent);
    string OptimizeJavaScriptForMobile(string jsContent);
    DeviceInfo DetectDevice(HttpRequest request);
    string GenerateResponsiveMetaTags();
    string GenerateMobileOptimizedStyles();
    string GenerateTouchOptimizedElements();
    MobileOptimizationReport GenerateOptimizationReport(string content);
}

/// <summary>
/// デバイス情報
/// </summary>
public class DeviceInfo
{
    public DeviceType Type { get; set; }
    public string UserAgent { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Browser { get; set; } = string.Empty;
    public bool IsMobile { get; set; }
    public bool IsTablet { get; set; }
    public bool IsDesktop { get; set; }
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public bool SupportsTouch { get; set; }
    public bool SupportsWebP { get; set; }
    public string PreferredLanguage { get; set; } = string.Empty;
}

/// <summary>
/// デバイスタイプ
/// </summary>
public enum DeviceType
{
    Mobile,
    Tablet,
    Desktop,
    Unknown
}

/// <summary>
/// モバイル最適化レポート
/// </summary>
public class MobileOptimizationReport
{
    public bool IsMobileOptimized { get; set; }
    public int OptimizationScore { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public Dictionary<string, string> Metrics { get; set; } = new();
}

/// <summary>
/// モバイル最適化サービス実装
/// </summary>
public class MobileOptimizationService : IMobileOptimizationService
{
    private readonly Dictionary<string, DeviceInfo> _deviceCache = new();

    public string OptimizeHtmlForMobile(string htmlContent)
    {
        var optimized = htmlContent;

        try
        {
            // ビューポートメタタグの追加・最適化
            if (!optimized.Contains("viewport"))
            {
                optimized = optimized.Replace("<head>", "<head>\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, maximum-scale=5.0\">");
            }

            // モバイル向けの追加メタタグ
            var mobileMetaTags = @"
    <meta name=""format-detection"" content=""telephone=no"">
    <meta name=""mobile-web-app-capable"" content=""yes"">
    <meta name=""apple-mobile-web-app-capable"" content=""yes"">
    <meta name=""apple-mobile-web-app-status-bar-style"" content=""black-translucent"">
    <meta name=""theme-color"" content=""#000000"">
    <link rel=""apple-touch-icon"" sizes=""180x180"" href=""/apple-touch-icon.png"">
    <link rel=""icon"" type=""image/png"" sizes=""32x32"" href=""/favicon-32x32.png"">
    <link rel=""icon"" type=""image/png"" sizes=""16x16"" href=""/favicon-16x16.png"">
    <link rel=""manifest"" href=""/manifest.json"">";

            if (!optimized.Contains("format-detection"))
            {
                optimized = optimized.Replace("<head>", $"<head>{mobileMetaTags}");
            }

            // モバイル向けの構造最適化
            optimized = OptimizeMobileStructure(optimized);

            return optimized;
        }
        catch (Exception ex)
        {
            // エラーが発生した場合は元のコンテンツを返す
            return htmlContent;
        }
    }

    public string OptimizeCssForMobile(string cssContent)
    {
        var optimized = cssContent;

        try
        {
            // モバイルファーストのメディアクエリを追加
            var mobileFirstCss = @"
/* Mobile First Design */
* {
    box-sizing: border-box;
}

/* Base styles for mobile */
body {
    font-size: 16px;
    line-height: 1.5;
    margin: 0;
    padding: 0;
}

/* Mobile styles (default) */
.mobile-only {
    display: block;
}

.desktop-only {
    display: none;
}

/* Touch targets - minimum 44px */
button, .btn, input[type=""submit""], input[type=""button""], a[role=""button""] {
    min-height: 44px;
    min-width: 44px;
    padding: 12px 16px;
}

/* Responsive images */
img {
    max-width: 100%;
    height: auto;
}

/* Tablet styles */
@media (min-width: 768px) {
    body {
        font-size: 18px;
    }

    .mobile-only {
        display: none;
    }

    .tablet-only {
        display: block;
    }
}

/* Desktop styles */
@media (min-width: 1024px) {
    body {
        font-size: 20px;
        max-width: 1200px;
        margin: 0 auto;
    }

    .desktop-only {
        display: block;
    }

    .tablet-only {
        display: none;
    }
}

/* High DPI displays */
@media (-webkit-min-device-pixel-ratio: 2), (min-resolution: 192dpi) {
    /* Retina-specific styles */
}

/* Touch device optimizations */
@media (pointer: coarse) {
    /* Larger touch targets and hover states */
    button, .btn {
        padding: 16px 24px;
        font-size: 18px;
    }
}

/* Reduced motion preference */
@media (prefers-reduced-motion: reduce) {
    * {
        animation-duration: 0.01ms !important;
        animation-iteration-count: 1 !important;
        transition-duration: 0.01ms !important;
    }
}

/* Dark mode support */
@media (prefers-color-scheme: dark) {
    /* Dark mode styles */
}

/* Print styles */
@media print {
    .no-print {
        display: none !important;
    }

    body {
        font-size: 12pt;
        line-height: 1.4;
    }
}";

            optimized = mobileFirstCss + "\n" + optimized;

            // モバイル向けの追加最適化
            optimized = OptimizeMobileSpecificCss(optimized);

            return optimized;
        }
        catch (Exception ex)
        {
            return cssContent;
        }
    }

    public string OptimizeJavaScriptForMobile(string jsContent)
    {
        var optimized = jsContent;

        try
        {
            // モバイル向けのJavaScript最適化を追加
            var mobileOptimizations = @"
// Mobile optimization utilities
(function() {
    'use strict';

    // Device detection
    window.isMobile = function() {
        return /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent);
    };

    window.isTablet = function() {
        return /iPad|Android(?!.*Mobile)/i.test(navigator.userAgent);
    };

    // Touch event optimization
    if ('ontouchstart' in window) {
        document.body.classList.add('touch-device');

        // Improve touch responsiveness
        document.addEventListener('touchstart', function() {}, { passive: true });
        document.addEventListener('touchmove', function() {}, { passive: true });
    }

    // Viewport height fix for mobile browsers
    function setVH() {
        let vh = window.innerHeight * 0.01;
        document.documentElement.style.setProperty('--vh', `${vh}px`);
    }

    setVH();
    window.addEventListener('resize', setVH);
    window.addEventListener('orientationchange', setVH);

    // Lazy loading for images
    if ('IntersectionObserver' in window) {
        const imageObserver = new IntersectionObserver((entries, observer) => {
            entries.forEach(entry => {
                if (entry.isIntersecting) {
                    const img = entry.target;
                    img.src = img.dataset.src;
                    img.classList.remove('lazy');
                    imageObserver.unobserve(img);
                }
            });
        });

        document.querySelectorAll('img[data-src]').forEach(img => {
            imageObserver.observe(img);
        });
    }

    // Service Worker registration for offline support
    if ('serviceWorker' in navigator) {
        window.addEventListener('load', function() {
            navigator.serviceWorker.register('/sw.js')
                .then(function(registration) {
                    console.log('ServiceWorker registration successful');
                })
                .catch(function(error) {
                    console.log('ServiceWorker registration failed');
                });
        });
    }

    // Mobile-specific performance optimizations
    if (window.isMobile()) {
        // Disable animations on mobile for better performance
        document.body.classList.add('mobile-optimized');

        // Reduce image quality for faster loading
        document.querySelectorAll('img').forEach(img => {
            if (!img.hasAttribute('data-no-optimize')) {
                img.style.imageRendering = 'auto';
            }
        });
    }

    // Accessibility improvements
    document.addEventListener('keydown', function(e) {
        // Escape key handling for modals
        if (e.key === 'Escape') {
            const openModal = document.querySelector('.modal.open');
            if (openModal) {
                closeModal(openModal);
            }
        }

        // Tab navigation improvements
        if (e.key === 'Tab') {
            document.body.classList.add('keyboard-navigation');
        }
    });

    document.addEventListener('mousedown', function() {
        document.body.classList.remove('keyboard-navigation');
    });

    // Mobile form improvements
    const forms = document.querySelectorAll('form');
    forms.forEach(form => {
        // Improve input types for mobile
        const inputs = form.querySelectorAll('input[type=""text""]');
        inputs.forEach(input => {
            if (input.name.includes('email')) {
                input.type = 'email';
            } else if (input.name.includes('phone') || input.name.includes('tel')) {
                input.type = 'tel';
            } else if (input.name.includes('url')) {
                input.type = 'url';
            }
        });

        // Auto-advance for numeric inputs
        const numericInputs = form.querySelectorAll('input[type=""number""], input[type=""tel""]');
        numericInputs.forEach(input => {
            input.addEventListener('input', function() {
                if (this.value.length >= this.maxLength && this.maxLength > 0) {
                    const nextInput = this.nextElementSibling;
                    if (nextInput && nextInput.tagName === 'INPUT') {
                        nextInput.focus();
                    }
                }
            });
        });
    });

    // Mobile-specific error handling
    window.addEventListener('error', function(e) {
        console.error('Mobile JavaScript error:', e.error);
        // Send error reports in production
        if (window.location.hostname !== 'localhost') {
            // reportError(e.error);
        }
    });

    // Mobile performance monitoring
    if ('performance' in window) {
        window.addEventListener('load', function() {
            setTimeout(function() {
                const perfData = performance.getEntriesByType('navigation')[0];
                if (perfData) {
                    console.log('Page load time:', perfData.loadEventEnd - perfData.fetchStart, 'ms');
                    // Send performance metrics in production
                }
            }, 0);
        });
    }

})();
";

            optimized = mobileOptimizations + "\n" + optimized;

            return optimized;
        }
        catch (Exception ex)
        {
            return jsContent;
        }
    }

    public DeviceInfo DetectDevice(HttpRequest request)
    {
        var userAgent = request.Headers["User-Agent"].ToString();
        var cacheKey = userAgent.GetHashCode().ToString();

        if (_deviceCache.TryGetValue(cacheKey, out var cachedDevice))
        {
            return cachedDevice;
        }

        var device = new DeviceInfo
        {
            UserAgent = userAgent,
            Platform = GetPlatformFromUserAgent(userAgent),
            Browser = GetBrowserFromUserAgent(userAgent),
            IsMobile = IsMobileDevice(userAgent),
            IsTablet = IsTabletDevice(userAgent),
            IsDesktop = IsDesktopDevice(userAgent),
            SupportsTouch = SupportsTouch(userAgent),
            SupportsWebP = SupportsWebP(userAgent),
            PreferredLanguage = request.Headers["Accept-Language"].FirstOrDefault() ?? "en-US"
        };

        // 画面サイズの検出（簡易版）
        var screenWidthMatch = System.Text.RegularExpressions.Regex.Match(userAgent, @"ScreenSize/(\d+)");
        if (screenWidthMatch.Success)
        {
            device.ScreenWidth = int.Parse(screenWidthMatch.Groups[1].Value);
            device.ScreenHeight = device.ScreenWidth * 4 / 3; // アスペクト比の推定
        }
        else
        {
            // デフォルト値
            if (device.IsMobile)
            {
                device.ScreenWidth = 375;
                device.ScreenHeight = 667;
            }
            else if (device.IsTablet)
            {
                device.ScreenWidth = 768;
                device.ScreenHeight = 1024;
            }
            else
            {
                device.ScreenWidth = 1920;
                device.ScreenHeight = 1080;
            }
        }

        // デバイスタイプの設定
        if (device.IsMobile)
        {
            device.Type = DeviceType.Mobile;
        }
        else if (device.IsTablet)
        {
            device.Type = DeviceType.Tablet;
        }
        else
        {
            device.Type = DeviceType.Desktop;
        }

        _deviceCache[cacheKey] = device;
        return device;
    }

    public string GenerateResponsiveMetaTags()
    {
        return @"<!-- Responsive and Mobile Optimization Meta Tags -->
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes"">
<meta name=""format-detection"" content=""telephone=no"">
<meta name=""mobile-web-app-capable"" content=""yes"">
<meta name=""apple-mobile-web-app-capable"" content=""yes"">
<meta name=""apple-mobile-web-app-status-bar-style"" content=""black-translucent"">
<meta name=""theme-color"" content=""#000000"">
<meta name=""msapplication-TileColor"" content=""#000000"">
<meta name=""msapplication-tap-highlight"" content=""no"">

<!-- Preload critical resources -->
<link rel=""preload"" href=""/css/critical.css"" as=""style"">
<link rel=""preload"" href=""/js/critical.js"" as=""script"">

<!-- DNS prefetch for external resources -->
<link rel=""dns-prefetch"" href=""//fonts.googleapis.com"">
<link rel=""dns-prefetch"" href=""//www.google-analytics.com"">

<!-- Favicons and icons -->
<link rel=""apple-touch-icon"" sizes=""180x180"" href=""/apple-touch-icon.png"">
<link rel=""icon"" type=""image/png"" sizes=""32x32"" href=""/favicon-32x32.png"">
<link rel=""icon"" type=""image/png"" sizes=""16x16"" href=""/favicon-16x16.png"">
<link rel=""manifest"" href=""/manifest.json"">
<link rel=""mask-icon"" href=""/safari-pinned-tab.svg"" color=""#000000"">

<!-- Open Graph and Twitter Card meta tags for social sharing -->
<meta property=""og:type"" content=""website"">
<meta property=""og:title"" content=""Potion Service"">
<meta property=""og:description"" content=""Enterprise-grade service management platform"">
<meta property=""og:url"" content=""https://potion.service.com"">
<meta property=""og:image"" content=""https://potion.service.com/og-image.png"">
<meta name=""twitter:card"" content=""summary_large_image"">
<meta name=""twitter:title"" content=""Potion Service"">
<meta name=""twitter:description"" content=""Enterprise-grade service management platform"">
<meta name=""twitter:image"" content=""https://potion.service.com/twitter-image.png"">";
    }

    public string GenerateMobileOptimizedStyles()
    {
        return @"/* Mobile-Optimized CSS Framework */

/* CSS Reset and Base Styles */
* {
    margin: 0;
    padding: 0;
    box-sizing: border-box;
}

html {
    font-size: 16px;
    line-height: 1.5;
    -webkit-text-size-adjust: 100%;
    -webkit-font-smoothing: antialiased;
    -moz-osx-font-smoothing: grayscale;
}

body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, sans-serif;
    color: #333;
    background-color: #fff;
}

/* Responsive Typography */
h1, h2, h3, h4, h5, h6 {
    margin-bottom: 0.5em;
    line-height: 1.2;
    font-weight: 600;
}

h1 { font-size: 2rem; }
h2 { font-size: 1.75rem; }
h3 { font-size: 1.5rem; }
h4 { font-size: 1.25rem; }
h5 { font-size: 1.125rem; }
h6 { font-size: 1rem; }

/* Responsive breakpoints */
.mobile-only { display: block; }
.tablet-only { display: none; }
.desktop-only { display: none; }

/* Mobile styles (default) */
@media (max-width: 767px) {
    .mobile-hidden { display: none !important; }
    .mobile-full-width { width: 100% !important; }
    .mobile-center { text-align: center !important; }
    .mobile-padding { padding: 1rem !important; }
    .mobile-margin { margin: 1rem !important; }
    .mobile-stack { flex-direction: column !important; }
}

/* Tablet styles */
@media (min-width: 768px) and (max-width: 1023px) {
    .mobile-only { display: none; }
    .tablet-only { display: block; }
    .tablet-hidden { display: none !important; }
    .tablet-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); }
}

/* Desktop styles */
@media (min-width: 1024px) {
    .desktop-only { display: block; }
    .tablet-only { display: none; }
    .desktop-hidden { display: none !important; }
    .desktop-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(400px, 1fr)); }
}

/* Touch-friendly interactive elements */
button, .btn, input[type=""submit""], input[type=""button""], a[role=""button""] {
    min-height: 44px;
    min-width: 44px;
    padding: 12px 16px;
    border: none;
    border-radius: 8px;
    font-size: 16px;
    font-weight: 500;
    text-decoration: none;
    display: inline-block;
    text-align: center;
    cursor: pointer;
    transition: all 0.2s ease;
    -webkit-tap-highlight-color: transparent;
}

button:focus, .btn:focus, input:focus {
    outline: 2px solid #007acc;
    outline-offset: 2px;
}

/* Form elements */
input, textarea, select {
    width: 100%;
    padding: 12px;
    border: 1px solid #ddd;
    border-radius: 6px;
    font-size: 16px;
    transition: border-color 0.2s ease;
}

input:focus, textarea:focus, select:focus {
    border-color: #007acc;
    outline: none;
}

/* Images */
img {
    max-width: 100%;
    height: auto;
    display: block;
}

/* Responsive containers */
.container {
    width: 100%;
    max-width: 1200px;
    margin: 0 auto;
    padding: 0 1rem;
}

.container.mobile { padding: 0 0.5rem; }
.container.tablet { padding: 0 2rem; }
.container.desktop { padding: 0 3rem; }

/* Responsive grid */
.grid {
    display: grid;
    gap: 1rem;
    grid-template-columns: 1fr;
}

@media (min-width: 768px) {
    .grid { grid-template-columns: repeat(2, 1fr); }
}

@media (min-width: 1024px) {
    .grid { grid-template-columns: repeat(3, 1fr); }
}

/* Navigation */
nav {
    background-color: #fff;
    box-shadow: 0 2px 4px rgba(0,0,0,0.1);
    position: sticky;
    top: 0;
    z-index: 100;
}

nav ul {
    list-style: none;
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    padding: 0.5rem 0;
}

nav li {
    flex: 1;
    min-width: fit-content;
}

nav a {
    display: block;
    padding: 0.75rem 1rem;
    text-decoration: none;
    color: #333;
    border-radius: 6px;
    transition: background-color 0.2s ease;
    text-align: center;
}

nav a:hover, nav a.active {
    background-color: #f0f0f0;
}

/* Cards and components */
.card {
    background: #fff;
    border-radius: 12px;
    box-shadow: 0 2px 8px rgba(0,0,0,0.1);
    padding: 1.5rem;
    margin-bottom: 1rem;
    transition: box-shadow 0.2s ease;
}

.card:hover {
    box-shadow: 0 4px 16px rgba(0,0,0,0.15);
}

/* Loading states */
.loading {
    display: flex;
    align-items: center;
    justify-content: center;
    min-height: 200px;
    font-size: 1.1rem;
    color: #666;
}

.spinner {
    width: 40px;
    height: 40px;
    border: 4px solid #f3f3f3;
    border-top: 4px solid #007acc;
    border-radius: 50%;
    animation: spin 1s linear infinite;
    margin-right: 1rem;
}

@keyframes spin {
    0% { transform: rotate(0deg); }
    100% { transform: rotate(360deg); }
}

/* Error states */
.error {
    background-color: #fee;
    border: 1px solid #fcc;
    color: #c33;
    padding: 1rem;
    border-radius: 6px;
    margin: 1rem 0;
}

/* Success states */
.success {
    background-color: #efe;
    border: 1px solid #cfc;
    color: #363;
    padding: 1rem;
    border-radius: 6px;
    margin: 1rem 0;
}

/* Utility classes */
.text-center { text-align: center; }
.text-left { text-align: left; }
.text-right { text-align: right; }

.mb-1 { margin-bottom: 0.5rem; }
.mb-2 { margin-bottom: 1rem; }
.mb-3 { margin-bottom: 1.5rem; }

.mt-1 { margin-top: 0.5rem; }
.mt-2 { margin-top: 1rem; }
.mt-3 { margin-top: 1.5rem; }

.p-1 { padding: 0.5rem; }
.p-2 { padding: 1rem; }
.p-3 { padding: 1.5rem; }

.d-none { display: none; }
.d-block { display: block; }
.d-flex { display: flex; }
.d-grid { display: grid; }

.flex-column { flex-direction: column; }
.flex-row { flex-direction: row; }
.justify-center { justify-content: center; }
.justify-between { justify-content: space-between; }
.align-center { align-items: center; }

.w-100 { width: 100%; }
.h-100 { height: 100%; }
.mw-100 { max-width: 100%; }
.mh-100 { max-height: 100%; }

/* Responsive utilities */
@media (max-width: 767px) {
    .mobile\:hidden { display: none !important; }
    .mobile\:block { display: block !important; }
    .mobile\:flex { display: flex !important; }
    .mobile\:text-center { text-align: center !important; }
    .mobile\:w-full { width: 100% !important; }
    .mobile\:p-2 { padding: 1rem !important; }
}

@media (min-width: 768px) {
    .tablet\:hidden { display: none !important; }
    .tablet\:block { display: block !important; }
    .tablet\:grid-cols-2 { grid-template-columns: repeat(2, 1fr) !important; }
}

@media (min-width: 1024px) {
    .desktop\:hidden { display: none !important; }
    .desktop\:block { display: block !important; }
    .desktop\:grid-cols-3 { grid-template-columns: repeat(3, 1fr) !important; }
}

/* Print styles */
@media print {
    .no-print { display: none !important; }
    .print\:block { display: block !important; }
    .print\:break-before { page-break-before: always; }
    .print\:break-after { page-break-after: always; }

    body { font-size: 12pt; line-height: 1.4; color: #000; }
    h1, h2, h3 { page-break-after: avoid; }
    img { max-width: 100%; height: auto; }
    .card { box-shadow: none; border: 1px solid #ddd; }
}

/* High contrast mode support */
@media (prefers-contrast: high) {
    button, .btn {
        border: 2px solid currentColor;
    }

    input, textarea, select {
        border: 2px solid currentColor;
    }
}

/* Reduced motion support */
@media (prefers-reduced-motion: reduce) {
    * {
        animation-duration: 0.01ms !important;
        animation-iteration-count: 1 !important;
        transition-duration: 0.01ms !important;
        scroll-behavior: auto !important;
    }
}

/* Dark mode support */
@media (prefers-color-scheme: dark) {
    body {
        background-color: #1a1a1a;
        color: #ffffff;
    }

    .card {
        background: #2d2d2d;
        color: #ffffff;
    }

    button, .btn {
        background-color: #007acc;
        color: #ffffff;
    }

    input, textarea, select {
        background-color: #2d2d2d;
        color: #ffffff;
        border-color: #555;
    }
}

/* Focus management for accessibility */
.focus-visible {
    outline: 2px solid #007acc;
    outline-offset: 2px;
}

.keyboard-navigation *:focus {
    outline: 2px solid #007acc;
    outline-offset: 2px;
}

/* Screen reader only content */
.sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    padding: 0;
    margin: -1px;
    overflow: hidden;
    clip: rect(0, 0, 0, 0);
    white-space: nowrap;
    border: 0;
}

/* Mobile-specific optimizations */
@media (max-width: 767px) {
    /* Larger touch targets */
    button, .btn, a {
        min-height: 48px;
        min-width: 48px;
        padding: 12px 24px;
    }

    /* Improved readability */
    body {
        font-size: 16px;
        line-height: 1.6;
    }

    /* Better spacing */
    .container {
        padding: 0 1rem;
    }

    /* Stack elements vertically */
    .mobile-stack > * + * {
        margin-top: 1rem;
    }
}

/* Touch device optimizations */
@media (pointer: coarse) {
    /* Remove hover effects on touch devices */
    .no-touch-hover:hover {
        transform: none;
        box-shadow: none;
    }

    /* Larger interactive areas */
    button, .btn, input, select, textarea {
        min-height: 44px;
    }
}

/* Performance optimizations */
.mobile-optimized img {
    image-rendering: -webkit-optimize-contrast;
    image-rendering: crisp-edges;
}

/* iOS specific fixes */
@supports (-webkit-touch-callout: none) {
    /* iOS specific styles */
    input, textarea {
        -webkit-appearance: none;
        border-radius: 0;
    }
}

/* Android specific fixes */
@media screen and (-webkit-min-device-pixel-ratio: 0) {
    /* Android specific styles */
    select {
        -webkit-appearance: none;
        background-image: none;
    }
}";
    }

    public string GenerateTouchOptimizedElements()
    {
        return @"<!-- Touch-Optimized HTML Elements -->

<!-- Touch-friendly buttons -->
<div class=""button-group"">
    <button class=""btn btn-primary touch-target"" aria-label=""Primary action"">
        <span class=""btn-text"">Primary Action</span>
    </button>

    <button class=""btn btn-secondary touch-target"" aria-label=""Secondary action"">
        <span class=""btn-text"">Secondary</span>
    </button>

    <a href=""#"" class=""btn btn-link touch-target"" role=""button"" aria-label=""Link action"">
        <span class=""btn-text"">Link Action</span>
    </a>
</div>

<!-- Touch-optimized form -->
<form class=""touch-form"" novalidate>
    <div class=""form-group"">
        <label for=""name"" class=""form-label"">
            Full Name <span class=""required"" aria-label=""required"">*</span>
        </label>
        <input
            type=""text""
            id=""name""
            name=""name""
            class=""form-input touch-input""
            required
            aria-describedby=""name-help""
            autocomplete=""name""
        >
        <div id=""name-help"" class=""form-help"">
            Enter your full legal name as it appears on your ID
        </div>
    </div>

    <div class=""form-group"">
        <label for=""email"" class=""form-label"">
            Email Address <span class=""required"" aria-label=""required"">*</span>
        </label>
        <input
            type=""email""
            id=""email""
            name=""email""
            class=""form-input touch-input""
            required
            aria-describedby=""email-help""
            autocomplete=""email""
            inputmode=""email""
        >
        <div id=""email-help"" class=""form-help"">
            We'll use this to send you important updates
        </div>
    </div>

    <div class=""form-group"">
        <label for=""phone"" class=""form-label"">
            Phone Number
        </label>
        <input
            type=""tel""
            id=""phone""
            name=""phone""
            class=""form-input touch-input""
            aria-describedby=""phone-help""
            autocomplete=""tel""
            inputmode=""tel""
            pattern=""[0-9]{3}-[0-9]{3}-[0-9]{4}""
        >
        <div id=""phone-help"" class=""form-help"">
            Format: 123-456-7890 (optional)
        </div>
    </div>

    <div class=""form-group"">
        <fieldset>
            <legend class=""form-label"">
                Preferred Contact Method <span class=""required"" aria-label=""required"">*</span>
            </legend>
            <div class=""radio-group touch-radio-group"">
                <label class=""radio-label touch-label"" for=""contact-email"">
                    <input type=""radio"" id=""contact-email"" name=""contact"" value=""email"" required>
                    <span class=""radio-custom touch-radio""></span>
                    <span class=""radio-text"">Email</span>
                </label>

                <label class=""radio-label touch-label"" for=""contact-phone"">
                    <input type=""radio"" id=""contact-phone"" name=""contact"" value=""phone"" required>
                    <span class=""radio-custom touch-radio""></span>
                    <span class=""radio-text"">Phone</span>
                </label>

                <label class=""radio-label touch-label"" for=""contact-text"">
                    <input type=""radio"" id=""contact-text"" name=""contact"" value=""text"" required>
                    <span class=""radio-custom touch-radio""></span>
                    <span class=""radio-text"">Text Message</span>
                </label>
            </div>
        </fieldset>
    </div>

    <div class=""form-group"">
        <label class=""checkbox-label touch-label"" for=""terms"">
            <input type=""checkbox"" id=""terms"" name=""terms"" value=""accepted"" required>
            <span class=""checkbox-custom touch-checkbox""></span>
            <span class=""checkbox-text"">
                I agree to the <a href=""#terms"" target=""_blank"">Terms of Service</a> and <a href=""#privacy"" target=""_blank"">Privacy Policy</a> <span class=""required"" aria-label=""required"">*</span>
            </span>
        </label>
    </div>

    <button type=""submit"" class=""btn btn-primary btn-large touch-target"" aria-describedby=""submit-help"">
        <span class=""btn-text"">Submit Form</span>
    </button>

    <div id=""submit-help"" class=""sr-only"">
        Submitting this form will process your information
    </div>
</form>

<!-- Touch-optimized navigation -->
<nav class=""touch-nav"" role=""navigation"" aria-label=""Main navigation"">
    <ul class=""nav-list touch-nav-list"" role=""menubar"">
        <li role=""none"">
            <a href=""#"" class=""nav-link touch-nav-link"" role=""menuitem"" aria-label=""Home page"">
                <span class=""nav-icon"" aria-hidden=""true"">🏠</span>
                <span class=""nav-text"">Home</span>
            </a>
        </li>

        <li role=""none"">
            <a href=""#services"" class=""nav-link touch-nav-link"" role=""menuitem"" aria-label=""Our services"">
                <span class=""nav-icon"" aria-hidden=""true"">🔧</span>
                <span class=""nav-text"">Services</span>
            </a>
        </li>

        <li role=""none"">
            <a href=""#contact"" class=""nav-link touch-nav-link"" role=""menuitem"" aria-label=""Contact us"">
                <span class=""nav-icon"" aria-hidden=""true"">📞</span>
                <span class=""nav-text"">Contact</span>
            </a>
        </li>

        <li role=""none"">
            <button class=""nav-link nav-menu-toggle touch-nav-link"" aria-expanded=""false"" aria-controls=""mobile-menu"" aria-label=""Open navigation menu"">
                <span class=""nav-icon"" aria-hidden=""true"">☰</span>
                <span class=""nav-text"">Menu</span>
            </button>
        </li>
    </ul>

    <!-- Mobile menu (hidden by default) -->
    <div id=""mobile-menu"" class=""mobile-menu"" aria-hidden=""true"">
        <ul class=""mobile-menu-list"" role=""menu"">
            <li role=""none""><a href=""#about"" class=""mobile-menu-link"" role=""menuitem"">About</a></li>
            <li role=""none""><a href=""#pricing"" class=""mobile-menu-link"" role=""menuitem"">Pricing</a></li>
            <li role=""none""><a href=""#support"" class=""mobile-menu-link"" role=""menuitem"">Support</a></li>
            <li role=""none""><a href=""#login"" class=""mobile-menu-link"" role=""menuitem"">Login</a></li>
        </ul>
    </div>
</nav>

<!-- Touch-optimized cards -->
<div class=""card-grid touch-card-grid"">
    <div class=""card touch-card"">
        <div class=""card-header"">
            <h3 class=""card-title"">Service Feature</h3>
        </div>
        <div class=""card-body"">
            <p class=""card-description"">Detailed description of the service feature and its benefits.</p>
        </div>
        <div class=""card-footer"">
            <button class=""btn btn-primary touch-target"" aria-label=""Learn more about this feature"">
                Learn More
            </button>
        </div>
    </div>

    <div class=""card touch-card"">
        <div class=""card-header"">
            <h3 class=""card-title"">Another Feature</h3>
        </div>
        <div class=""card-body"">
            <p class=""card-description"">Another detailed description with specific benefits and use cases.</p>
        </div>
        <div class=""card-footer"">
            <button class=""btn btn-secondary touch-target"" aria-label=""View details for this feature"">
                View Details
            </button>
        </div>
    </div>
</div>

<!-- Touch-optimized modal -->
<div id=""touch-modal"" class=""modal touch-modal"" role=""dialog"" aria-labelledby=""modal-title"" aria-hidden=""true"">
    <div class=""modal-overlay"" aria-hidden=""true""></div>
    <div class=""modal-content touch-modal-content"">
        <div class=""modal-header"">
            <h2 id=""modal-title"" class=""modal-title"">Modal Title</h2>
            <button class=""modal-close touch-modal-close"" aria-label=""Close modal"">
                <span aria-hidden=""true"">&times;</span>
            </button>
        </div>
        <div class=""modal-body"">
            <p>Modal content goes here. This content is optimized for touch interactions and screen readers.</p>
            <form class=""touch-form"">
                <div class=""form-group"">
                    <label for=""modal-input"" class=""form-label"">Input Field</label>
                    <input type=""text"" id=""modal-input"" class=""form-input touch-input"" placeholder=""Enter text here"">
                </div>
                <button type=""submit"" class=""btn btn-primary touch-target"">Submit</button>
            </form>
        </div>
        <div class=""modal-footer"">
            <button class=""btn btn-secondary touch-target"" aria-label=""Cancel and close modal"">Cancel</button>
            <button class=""btn btn-primary touch-target"" aria-label=""Confirm and close modal"">Confirm</button>
        </div>
    </div>
</div>

<!-- Touch-optimized tables (for larger screens) -->
<div class=""table-container touch-table-container"">
    <table class=""responsive-table touch-table"" role=""table"" aria-label=""Data table"">
        <caption class=""sr-only"">User data and statistics</caption>
        <thead>
            <tr role=""row"">
                <th role=""columnheader"" scope=""col"">Name</th>
                <th role=""columnheader"" scope=""col"">Email</th>
                <th role=""columnheader"" scope=""col"">Status</th>
                <th role=""columnheader"" scope=""col"">Actions</th>
            </tr>
        </thead>
        <tbody>
            <tr role=""row"">
                <td role=""cell"">John Doe</td>
                <td role=""cell"">john@example.com</td>
                <td role=""cell"">
                    <span class=""status-badge status-active"" aria-label=""Status: Active"">Active</span>
                </td>
                <td role=""cell"">
                    <div class=""button-group"">
                        <button class=""btn btn-sm btn-primary touch-target"" aria-label=""Edit user"">Edit</button>
                        <button class=""btn btn-sm btn-secondary touch-target"" aria-label=""Delete user"">Delete</button>
                    </div>
                </td>
            </tr>
        </tbody>
    </table>
</div>

<!-- Touch-optimized loading states -->
<div class=""loading-container touch-loading"">
    <div class=""loading-spinner touch-spinner"" aria-hidden=""true"">
        <div class=""spinner-ring""></div>
        <div class=""spinner-ring""></div>
        <div class=""spinner-ring""></div>
        <div class=""spinner-ring""></div>
    </div>
    <p class=""loading-text"">Loading content...</p>
    <p class=""loading-subtext sr-only"">Please wait while we load the content for you</p>
</div>

<!-- Touch-optimized error states -->
<div class=""error-container touch-error"">
    <div class=""error-icon touch-error-icon"" aria-hidden=""true"">⚠️</div>
    <h3 class=""error-title"">Something went wrong</h3>
    <p class=""error-message"">We're sorry, but something unexpected happened. Please try again or contact support if the problem persists.</p>
    <div class=""error-actions"">
        <button class=""btn btn-primary touch-target"" onclick=""location.reload()"">Try Again</button>
        <button class=""btn btn-secondary touch-target"" onclick=""contactSupport()"">Contact Support</button>
    </div>
</div>

<!-- Touch-optimized success states -->
<div class=""success-container touch-success"">
    <div class=""success-icon touch-success-icon"" aria-hidden=""true"">✅</div>
    <h3 class=""success-title"">Success!</h3>
    <p class=""success-message"">Your action was completed successfully.</p>
    <button class=""btn btn-primary touch-target"">Continue</button>
</div>";
    }

    public MobileOptimizationReport GenerateOptimizationReport(string content)
    {
        var report = new MobileOptimizationReport();

        try
        {
            // 基本的なチェック
            report.IsMobileOptimized = content.Contains("viewport") && content.Contains("mobile");
            report.OptimizationScore = CalculateOptimizationScore(content);

            // 問題の検出
            if (!content.Contains("viewport"))
            {
                report.Issues.Add("Missing viewport meta tag");
                report.Recommendations.Add("Add viewport meta tag for proper mobile rendering");
            }

            if (!content.Contains("touch"))
            {
                report.Issues.Add("Limited touch optimization");
                report.Recommendations.Add("Add touch-friendly interactive elements");
            }

            if (!content.Contains("apple-mobile-web-app"))
            {
                report.Issues.Add("Limited iOS optimization");
                report.Recommendations.Add("Add iOS-specific meta tags for better mobile experience");
            }

            // レスポンシブデザインのチェック
            var responsiveElements = new[] { "mobile-only", "tablet-only", "desktop-only", "media", "flex", "grid" };
            var responsiveCount = responsiveElements.Count(element => content.Contains(element));

            report.Metrics["ResponsiveElements"] = responsiveCount.ToString();
            report.Metrics["ContentLength"] = content.Length.ToString();

            if (responsiveCount < 3)
            {
                report.Issues.Add("Limited responsive design elements");
                report.Recommendations.Add("Implement more responsive design patterns");
            }

            return report;
        }
        catch (Exception ex)
        {
            report.Issues.Add($"Analysis error: {ex.Message}");
            return report;
        }
    }

    private string OptimizeMobileStructure(string html)
    {
        // モバイル向けの構造最適化
        var optimized = html;

        // モバイルナビゲーションの改善
        optimized = System.Text.RegularExpressions.Regex.Replace(optimized,
            @"<nav[^>]*>",
            "$0<!-- Mobile-optimized navigation -->",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return optimized;
    }

    private string OptimizeMobileSpecificCss(string css)
    {
        // モバイル特有のCSS最適化を追加
        var mobileOptimizations = @"
/* Mobile-specific performance optimizations */
@media (max-width: 767px) {
    /* Reduce animations for better performance */
    * {
        animation-duration: 0.3s !important;
    }

    /* Optimize images for mobile */
    img {
        image-rendering: auto;
    }

    /* Better font rendering */
    body {
        -webkit-font-smoothing: antialiased;
        -moz-osx-font-smoothing: grayscale;
        text-rendering: optimizeLegibility;
    }
}";

        return css + "\n" + mobileOptimizations;
    }

    private string GetPlatformFromUserAgent(string userAgent)
    {
        if (userAgent.Contains("Windows")) return "Windows";
        if (userAgent.Contains("Mac")) return "macOS";
        if (userAgent.Contains("Linux")) return "Linux";
        if (userAgent.Contains("Android")) return "Android";
        if (userAgent.Contains("iPhone") || userAgent.Contains("iPad")) return "iOS";
        return "Unknown";
    }

    private string GetBrowserFromUserAgent(string userAgent)
    {
        if (userAgent.Contains("Chrome")) return "Chrome";
        if (userAgent.Contains("Firefox")) return "Firefox";
        if (userAgent.Contains("Safari")) return "Safari";
        if (userAgent.Contains("Edge")) return "Edge";
        if (userAgent.Contains("Opera")) return "Opera";
        return "Unknown";
    }

    private bool IsMobileDevice(string userAgent)
    {
        return userAgent.Contains("Mobile") || userAgent.Contains("Android") ||
               userAgent.Contains("iPhone") || userAgent.Contains("BlackBerry") ||
               userAgent.Contains("Windows Phone");
    }

    private bool IsTabletDevice(string userAgent)
    {
        return (userAgent.Contains("iPad") || userAgent.Contains("Android")) &&
               !userAgent.Contains("Mobile");
    }

    private bool IsDesktopDevice(string userAgent)
    {
        return !IsMobileDevice(userAgent) && !IsTabletDevice(userAgent);
    }

    private bool SupportsTouch(string userAgent)
    {
        return userAgent.Contains("Touch") || userAgent.Contains("Mobile") ||
               userAgent.Contains("Android") || userAgent.Contains("iPhone") ||
               userAgent.Contains("iPad");
    }

    private bool SupportsWebP(string userAgent)
    {
        // 簡易的なチェック（実際の実装ではより詳細なチェックが必要）
        return userAgent.Contains("Chrome") || userAgent.Contains("Firefox") ||
               userAgent.Contains("Edge") || userAgent.Contains("Opera");
    }

    private int CalculateOptimizationScore(string content)
    {
        var score = 0;

        // 基本的な最適化チェックポイント
        if (content.Contains("viewport")) score += 20;
        if (content.Contains("mobile")) score += 15;
        if (content.Contains("touch")) score += 15;
        if (content.Contains("responsive")) score += 10;
        if (content.Contains("apple-mobile-web-app")) score += 10;
        if (content.Contains("format-detection")) score += 5;
        if (content.Contains("theme-color")) score += 5;
        if (content.Contains("manifest")) score += 5;
        if (content.Contains("service-worker")) score += 10;
        if (content.Contains("lazy")) score += 5;

        return Math.Min(score, 100);
    }
}
