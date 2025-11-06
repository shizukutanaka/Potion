using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Potion.Service.Infrastructure;

/// <summary>
/// オフライン機能の強化サービス
/// Service Workerとキャッシュ戦略の実装
/// </summary>
public interface IOfflineService
{
    string GenerateServiceWorker();
    string GenerateOfflinePage();
    string GenerateCacheManifest();
    string GenerateOfflineFallbackContent();
    OfflineConfiguration GetOfflineConfiguration();
    string GenerateOfflineDetectionScript();
    string GenerateSyncStrategyScript();
    Task<OfflineStatus> GetOfflineStatusAsync(HttpRequest request);
}

/// <summary>
/// オフライン設定
/// </summary>
public class OfflineConfiguration
{
    public bool EnableServiceWorker { get; set; } = true;
    public bool EnableBackgroundSync { get; set; } = true;
    public bool EnablePushNotifications { get; set; } = true;
    public int CacheExpirationHours { get; set; } = 24;
    public List<string> CachePatterns { get; set; } = new();
    public List<string> NetworkFirstPatterns { get; set; } = new();
    public List<string> CacheFirstPatterns { get; set; } = new();
    public string OfflinePagePath { get; set; } = "/offline.html";
    public Dictionary<string, string> FallbackContent { get; set; } = new();
}

/// <summary>
/// オフライン状態
/// </summary>
public class OfflineStatus
{
    public bool IsOnline { get; set; }
    public bool HasServiceWorker { get; set; }
    public bool HasCacheManifest { get; set; }
    public long CacheSize { get; set; }
    public int CachedResources { get; set; }
    public List<string> OfflineCapabilities { get; set; } = new();
}

/// <summary>
/// オフラインサービス実装
/// </summary>
public class OfflineService : IOfflineService
{
    private readonly OfflineConfiguration _configuration;

    public OfflineService()
    {
        _configuration = new OfflineConfiguration
        {
            EnableServiceWorker = true,
            EnableBackgroundSync = true,
            EnablePushNotifications = true,
            CacheExpirationHours = 24,
            CachePatterns = new List<string>
            {
                "/css/*",
                "/js/*",
                "/images/*",
                "/api/health",
                "/api/config"
            },
            NetworkFirstPatterns = new List<string>
            {
                "/api/*",
                "/user/*"
            },
            CacheFirstPatterns = new List<string>
            {
                "/css/*",
                "/js/*",
                "/images/*"
            },
            OfflinePagePath = "/offline.html",
            FallbackContent = new Dictionary<string, string>
            {
                ["/api/*"] = "/offline-api.html",
                ["/images/*"] = "/images/offline-placeholder.png"
            }
        };
    }

    public string GenerateServiceWorker()
    {
        return @"// Service Worker for Offline Support
const CACHE_NAME = 'potion-offline-cache-v1';
const OFFLINE_URL = '/offline.html';

// Resources to cache immediately
const PRECACHE_RESOURCES = [
    '/',
    '/offline.html',
    '/css/offline.css',
    '/js/offline.js',
    '/images/offline-icon.png',
    '/manifest.json'
];

// Resources to cache on first request
const RUNTIME_CACHE_PATTERNS = [
    /\.(?:css|js|png|jpg|jpeg|svg|gif|woff|woff2|ttf|eot)$/,
    /^https?:\/\/fonts\.googleapis\.com\/.*/,
    /^https?:\/\/cdnjs\.cloudflare\.com\/.*/
];

// Network-first patterns (API calls, user data)
const NETWORK_FIRST_PATTERNS = [
    /\/api\//,
    /\/user\//,
    /\/admin\//
];

// Cache-first patterns (static assets)
const CACHE_FIRST_PATTERNS = [
    /\/css\//,
    /\/js\//,
    /\/images\//,
    /\/fonts\//
];

// Install event - cache essential resources
self.addEventListener('install', event => {
    console.log('[ServiceWorker] Install');

    event.waitUntil(
        caches.open(CACHE_NAME)
            .then(cache => {
                console.log('[ServiceWorker] Pre-caching resources');
                return cache.addAll(PRECACHE_RESOURCES);
            })
            .then(() => {
                console.log('[ServiceWorker] Skip waiting');
                return self.skipWaiting();
            })
    );
});

// Activate event - clean up old caches
self.addEventListener('activate', event => {
    console.log('[ServiceWorker] Activate');

    event.waitUntil(
        caches.keys().then(cacheNames => {
            return Promise.all(
                cacheNames.map(cacheName => {
                    if (cacheName !== CACHE_NAME) {
                        console.log('[ServiceWorker] Deleting old cache:', cacheName);
                        return caches.delete(cacheName);
                    }
                })
            );
        }).then(() => {
            console.log('[ServiceWorker] Claim clients');
            return self.clients.claim();
        })
    );
});

// Fetch event - implement caching strategies
self.addEventListener('fetch', event => {
    const { request } = event;
    const url = new URL(request.url);

    // Skip non-GET requests
    if (request.method !== 'GET') {
        return;
    }

    // Skip cross-origin requests (except fonts and CDN)
    if (url.origin !== location.origin && !isExternalResource(url)) {
        return;
    }

    // Determine caching strategy based on URL pattern
    if (shouldUseNetworkFirst(request.url)) {
        event.respondWith(networkFirstStrategy(request));
    } else if (shouldUseCacheFirst(request.url)) {
        event.respondWith(cacheFirstStrategy(request));
    } else {
        event.respondWith(staleWhileRevalidateStrategy(request));
    }
});

// Network-first strategy (for dynamic content)
async function networkFirstStrategy(request) {
    try {
        // Try network first
        const networkResponse = await fetch(request);

        if (networkResponse.ok) {
            const cache = await caches.open(CACHE_NAME);
            cache.put(request, networkResponse.clone());
        }

        return networkResponse;
    } catch (error) {
        console.log('[ServiceWorker] Network failed, trying cache:', request.url);

        // Network failed, try cache
        const cachedResponse = await caches.match(request);

        if (cachedResponse) {
            return cachedResponse;
        }

        // No cache available, return offline fallback
        return getOfflineFallback(request);
    }
}

// Cache-first strategy (for static assets)
async function cacheFirstStrategy(request) {
    // Try cache first
    const cachedResponse = await caches.match(request);

    if (cachedResponse) {
        return cachedResponse;
    }

    // Not in cache, try network
    try {
        const networkResponse = await fetch(request);

        if (networkResponse.ok) {
            const cache = await caches.open(CACHE_NAME);
            cache.put(request, networkResponse.clone());
        }

        return networkResponse;
    } catch (error) {
        console.log('[ServiceWorker] Cache and network failed:', request.url);
        return getOfflineFallback(request);
    }
}

// Stale-while-revalidate strategy (balanced approach)
async function staleWhileRevalidateStrategy(request) {
    const cache = await caches.open(CACHE_NAME);
    const cachedResponse = await cache.match(request);

    // Start network request (don't await)
    const networkPromise = fetch(request).then(networkResponse => {
        if (networkResponse.ok) {
            cache.put(request, networkResponse.clone());
        }
        return networkResponse;
    }).catch(() => null);

    // Return cached version immediately if available
    if (cachedResponse) {
        return cachedResponse;
    }

    // No cache, wait for network
    return networkPromise || getOfflineFallback(request);
}

// Background sync for offline actions
self.addEventListener('sync', event => {
    console.log('[ServiceWorker] Background sync:', event.tag);

    if (event.tag === 'background-sync') {
        event.waitUntil(syncOfflineActions());
    }
});

// Push notifications
self.addEventListener('push', event => {
    console.log('[ServiceWorker] Push received');

    const options = {
        body: event.data ? event.data.text() : 'New notification',
        icon: '/images/notification-icon.png',
        badge: '/images/badge-icon.png',
        vibrate: [100, 50, 100],
        data: {
            dateOfArrival: Date.now(),
            primaryKey: 1
        },
        actions: [
            {
                action: 'explore',
                title: 'View Details',
                icon: '/images/checkmark.png'
            },
            {
                action: 'close',
                title: 'Close',
                icon: '/images/xmark.png'
            }
        ]
    };

    event.waitUntil(
        self.registration.showNotification('Potion Service', options)
    );
});

// Notification click handler
self.addEventListener('notificationclick', event => {
    console.log('[ServiceWorker] Notification click received');

    event.notification.close();

    if (event.action === 'explore') {
        event.waitUntil(
            clients.openWindow('/')
        );
    }
});

// Helper functions
function shouldUseNetworkFirst(url) {
    return NETWORK_FIRST_PATTERNS.some(pattern => pattern.test(url));
}

function shouldUseCacheFirst(url) {
    return CACHE_FIRST_PATTERNS.some(pattern => pattern.test(url));
}

function isExternalResource(url) {
    const externalPatterns = [
        /^https?:\/\/fonts\.googleapis\.com\/.*/,
        /^https?:\/\/cdnjs\.cloudflare\.com\/.*/
    ];

    return externalPatterns.some(pattern => pattern.test(url.href));
}

async function getOfflineFallback(request) {
    const url = new URL(request.url);

    // API fallback
    if (url.pathname.startsWith('/api/')) {
        return new Response(JSON.stringify({
            error: 'Offline',
            message: 'This content is not available offline',
            offline: true
        }), {
            status: 503,
            statusText: 'Service Unavailable',
            headers: { 'Content-Type': 'application/json' }
        });
    }

    // Image fallback
    if (url.pathname.startsWith('/images/')) {
        return caches.match('/images/offline-placeholder.png');
    }

    // Default offline page
    return caches.match(OFFLINE_URL);
}

async function syncOfflineActions() {
    console.log('[ServiceWorker] Syncing offline actions');

    // Get offline actions from IndexedDB
    const offlineActions = await getOfflineActions();

    for (const action of offlineActions) {
        try {
            await syncAction(action);
            await removeOfflineAction(action.id);
        } catch (error) {
            console.error('[ServiceWorker] Failed to sync action:', action.id, error);
        }
    }
}

async function getOfflineActions() {
    // IndexedDBからオフラインアクションを取得
    return [];
}

async function syncAction(action) {
    // オフラインアクションを同期
    console.log('[ServiceWorker] Syncing action:', action);
}

async function removeOfflineAction(actionId) {
    // オフラインアクションを削除
    console.log('[ServiceWorker] Removing offline action:', actionId);
}

// Message handling for communication with main thread
self.addEventListener('message', event => {
    if (event.data && event.data.type === 'SKIP_WAITING') {
        self.skipWaiting();
    }

    if (event.data && event.data.type === 'GET_CACHE_SIZE') {
        event.ports[0].postMessage({ cacheSize: getCacheSize() });
    }
});

async function getCacheSize() {
    const cacheNames = await caches.keys();
    let totalSize = 0;

    for (const cacheName of cacheNames) {
        const cache = await caches.open(cacheName);
        const requests = await cache.keys();

        for (const request of requests) {
            try {
                const response = await cache.match(request);
                if (response) {
                    const blob = await response.blob();
                    totalSize += blob.size;
                }
            } catch (e) {
                // Skip responses that can't be sized
            }
        }
    }

    return totalSize;
}

// Periodic cache cleanup
self.addEventListener('message', event => {
    if (event.data && event.data.type === 'CLEANUP_CACHE') {
        cleanupOldCaches();
    }
});

async function cleanupOldCaches() {
    const cacheNames = await caches.keys();
    const currentCache = 'potion-offline-cache-v1';

    return Promise.all(
        cacheNames.map(cacheName => {
            if (cacheName !== currentCache) {
                console.log('[ServiceWorker] Deleting old cache:', cacheName);
                return caches.delete(cacheName);
            }
        })
    );
}

// Error handling
self.addEventListener('error', event => {
    console.error('[ServiceWorker] Error:', event.error);
});

self.addEventListener('unhandledrejection', event => {
    console.error('[ServiceWorker] Unhandled promise rejection:', event.reason);
    event.preventDefault();
});";
    }

    public string GenerateOfflinePage()
    {
        return @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Offline - Potion Service</title>
    <style>
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }

        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            text-align: center;
            padding: 2rem;
        }

        .offline-container {
            max-width: 600px;
            background: rgba(255, 255, 255, 0.1);
            backdrop-filter: blur(10px);
            border-radius: 20px;
            padding: 3rem 2rem;
            box-shadow: 0 20px 40px rgba(0, 0, 0, 0.1);
        }

        .offline-icon {
            font-size: 4rem;
            margin-bottom: 1rem;
            opacity: 0.8;
        }

        h1 {
            font-size: 2.5rem;
            margin-bottom: 1rem;
            font-weight: 300;
        }

        p {
            font-size: 1.1rem;
            line-height: 1.6;
            margin-bottom: 2rem;
            opacity: 0.9;
        }

        .features {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
            gap: 1.5rem;
            margin: 2rem 0;
        }

        .feature {
            background: rgba(255, 255, 255, 0.05);
            padding: 1.5rem;
            border-radius: 12px;
            border: 1px solid rgba(255, 255, 255, 0.1);
        }

        .feature-icon {
            font-size: 2rem;
            margin-bottom: 0.5rem;
        }

        .feature-title {
            font-size: 1.1rem;
            font-weight: 600;
            margin-bottom: 0.5rem;
        }

        .feature-description {
            font-size: 0.9rem;
            opacity: 0.8;
        }

        .retry-btn {
            background: rgba(255, 255, 255, 0.2);
            color: white;
            border: 2px solid rgba(255, 255, 255, 0.3);
            padding: 1rem 2rem;
            font-size: 1rem;
            border-radius: 50px;
            cursor: pointer;
            transition: all 0.3s ease;
            margin-top: 1rem;
            text-decoration: none;
            display: inline-block;
        }

        .retry-btn:hover {
            background: rgba(255, 255, 255, 0.3);
            border-color: rgba(255, 255, 255, 0.5);
            transform: translateY(-2px);
        }

        .connection-status {
            margin-top: 2rem;
            padding: 1rem;
            border-radius: 8px;
            background: rgba(255, 255, 255, 0.1);
            font-size: 0.9rem;
        }

        .status-indicator {
            display: inline-block;
            width: 12px;
            height: 12px;
            border-radius: 50%;
            background: #ff6b6b;
            margin-right: 0.5rem;
            animation: pulse 2s infinite;
        }

        .status-indicator.online {
            background: #51cf66;
        }

        @keyframes pulse {
            0% { opacity: 1; }
            50% { opacity: 0.5; }
            100% { opacity: 1; }
        }

        @media (max-width: 768px) {
            .offline-container {
                margin: 1rem;
                padding: 2rem 1.5rem;
            }

            h1 {
                font-size: 2rem;
            }

            .features {
                grid-template-columns: 1fr;
                gap: 1rem;
            }
        }
    </style>
</head>
<body>
    <div class=""offline-container"">
        <div class=""offline-icon"">📡</div>
        <h1>You're Offline</h1>
        <p>It looks like you've lost your internet connection. Don't worry - some features are still available while you're offline.</p>

        <div class=""features"">
            <div class=""feature"">
                <div class=""feature-icon"">💾</div>
                <div class=""feature-title"">Cached Content</div>
                <div class=""feature-description"">Previously visited pages and content are available offline</div>
            </div>

            <div class=""feature"">
                <div class=""feature-icon"">📝</div>
                <div class=""feature-title"">Offline Forms</div>
                <div class=""feature-description"">Form data is saved locally and synced when back online</div>
            </div>

            <div class=""feature"">
                <div class=""feature-icon"">🔄</div>
                <div class=""feature-title"">Background Sync</div>
                <div class=""feature-description"">Actions performed offline will sync automatically</div>
            </div>
        </div>

        <button class=""retry-btn"" onclick=""retryConnection()"">
            Try Again
        </button>

        <div class=""connection-status"" id=""connectionStatus"">
            <span class=""status-indicator"" id=""statusIndicator""></span>
            <span id=""statusText"">Checking connection...</span>
        </div>
    </div>

    <script>
        // Connection status monitoring
        function updateConnectionStatus() {
            const statusIndicator = document.getElementById('statusIndicator');
            const statusText = document.getElementById('statusText');

            if (navigator.onLine) {
                statusIndicator.className = 'status-indicator online';
                statusText.textContent = 'Connected to internet';
            } else {
                statusIndicator.className = 'status-indicator';
                statusText.textContent = 'No internet connection';
            }
        }

        function retryConnection() {
            if (navigator.onLine) {
                // Reload the page when back online
                window.location.reload();
            } else {
                // Show a message that we're still offline
                alert('Still offline. Please check your internet connection and try again.');
            }
        }

        // Event listeners
        window.addEventListener('online', updateConnectionStatus);
        window.addEventListener('offline', updateConnectionStatus);

        // Initialize status
        updateConnectionStatus();

        // Periodic connection check
        setInterval(() => {
            if (navigator.onLine) {
                updateConnectionStatus();
            }
        }, 5000);

        // Service Worker communication
        if ('serviceWorker' in navigator) {
            navigator.serviceWorker.ready.then(registration => {
                console.log('Service Worker is ready for offline support');
            });
        }

        // Background sync for offline actions
        if ('serviceWorker' in navigator && 'sync' in window.ServiceWorkerRegistration.prototype) {
            navigator.serviceWorker.ready.then(registration => {
                return registration.sync.register('background-sync');
            }).catch(error => {
                console.log('Background sync registration failed:', error);
            });
        }

        // Push notification subscription (optional)
        if ('serviceWorker' in navigator && 'PushManager' in window) {
            // Push notification setup would go here
        }
    </script>
</body>
</html>";
    }

    public string GenerateCacheManifest()
    {
        return @"CACHE MANIFEST
# Potion Service Cache Manifest v1.0.0
# Generated: """ + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + @"""

CACHE:
# Core application files
/
/index.html
/css/app.css
/js/app.js
/js/vendors.js
/manifest.json

# Offline fallback page
/offline.html

# Images and icons
/images/logo.png
/images/favicon.ico
/images/offline-icon.png
/images/offline-placeholder.png
/icons/icon-192x192.png
/icons/icon-512x512.png

# Fonts (if any)
/fonts/roboto-regular.woff2
/fonts/roboto-medium.woff2

# API endpoints that should be cached
/api/health
/api/config

NETWORK:
# Always fetch fresh data for these patterns
/api/*
/user/*
/admin/*

FALLBACK:
/api/ /offline-api.html
/images/ /images/offline-placeholder.png
/ /offline.html

# Cache control
# Files listed here will be cached for 24 hours
# After that, they'll be revalidated with the server
# Network requests will always go to the server first

# Version comment for cache busting
# Update this version number when you want to invalidate all caches
# Version: 1.0.0";
    }

    public string GenerateOfflineFallbackContent()
    {
        return @"<!-- Offline API Fallback Content -->
<div class=""offline-api-fallback"">
    <div class=""offline-api-header"">
        <h3>Content Not Available Offline</h3>
        <p>This content requires an internet connection to load.</p>
    </div>

    <div class=""offline-api-features"">
        <div class=""feature"">
            <div class=""feature-icon"">🔄</div>
            <div class=""feature-title"">Auto-Sync</div>
            <div class=""feature-description"">Content will load automatically when you're back online</div>
        </div>

        <div class=""feature"">
            <div class=""feature-icon"">💾</div>
            <div class=""feature-title"">Cached Data</div>
            <div class=""feature-description"">Previously loaded data may still be available</div>
        </div>

        <div class=""feature"">
            <div class=""feature-icon"">📱</div>
            <div class=""feature-title"">Offline Mode</div>
            <div class=""feature-description"">Some features work without internet connection</div>
        </div>
    </div>

    <button class=""retry-btn"" onclick=""retryLoad()"">
        Retry Loading
    </button>
</div>

<!-- Offline Image Placeholder -->
<div class=""offline-image-placeholder"">
    <div class=""placeholder-content"">
        <div class=""placeholder-icon"">🖼️</div>
        <div class=""placeholder-text"">Image not available offline</div>
        <div class=""placeholder-subtext"">This image will load when you're back online</div>
    </div>
</div>

<style>
.offline-api-fallback,
.offline-image-placeholder {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    padding: 2rem;
    background: rgba(255, 255, 255, 0.05);
    border-radius: 12px;
    border: 1px solid rgba(255, 255, 255, 0.1);
    color: white;
    text-align: center;
}

.offline-api-header {
    margin-bottom: 2rem;
}

.offline-api-header h3 {
    margin-bottom: 0.5rem;
    font-size: 1.5rem;
    font-weight: 600;
}

.offline-api-features {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: 1rem;
    margin: 2rem 0;
    width: 100%;
}

.feature {
    padding: 1rem;
    background: rgba(255, 255, 255, 0.05);
    border-radius: 8px;
    border: 1px solid rgba(255, 255, 255, 0.1);
}

.feature-icon {
    font-size: 2rem;
    margin-bottom: 0.5rem;
}

.feature-title {
    font-weight: 600;
    margin-bottom: 0.5rem;
}

.feature-description {
    font-size: 0.9rem;
    opacity: 0.8;
}

.retry-btn {
    background: rgba(255, 255, 255, 0.2);
    color: white;
    border: 2px solid rgba(255, 255, 255, 0.3);
    padding: 1rem 2rem;
    font-size: 1rem;
    border-radius: 50px;
    cursor: pointer;
    transition: all 0.3s ease;
    margin-top: 1rem;
}

.retry-btn:hover {
    background: rgba(255, 255, 255, 0.3);
    border-color: rgba(255, 255, 255, 0.5);
}

.placeholder-content {
    text-align: center;
}

.placeholder-icon {
    font-size: 3rem;
    margin-bottom: 1rem;
    opacity: 0.7;
}

.placeholder-text {
    font-size: 1.1rem;
    font-weight: 600;
    margin-bottom: 0.5rem;
}

.placeholder-subtext {
    font-size: 0.9rem;
    opacity: 0.7;
}
</style>

<script>
function retryLoad() {
    if (navigator.onLine) {
        window.location.reload();
    } else {
        alert('Still offline. Please check your internet connection.');
    }
}

// Monitor online status for auto-retry
window.addEventListener('online', () => {
    console.log('Back online - content should reload automatically');
    setTimeout(() => {
        if (window.location.pathname.startsWith('/api/')) {
            window.location.reload();
        }
    }, 1000);
});
</script>";
    }

    public OfflineConfiguration GetOfflineConfiguration()
    {
        return _configuration;
    }

    public string GenerateOfflineDetectionScript()
    {
        return @"// Offline Detection and Management Script
(function() {
    'use strict';

    // Offline detection utilities
    window.OfflineManager = {
        isOnline: navigator.onLine,
        hasServiceWorker: 'serviceWorker' in navigator,
        hasBackgroundSync: 'sync' in window.ServiceWorkerRegistration.prototype,
        hasPushNotifications: 'PushManager' in window,

        // Initialize offline functionality
        init: function() {
            this.setupEventListeners();
            this.updateOnlineStatus();
            this.initializeServiceWorker();
            this.setupBackgroundSync();
            this.setupPushNotifications();
        },

        // Set up event listeners for online/offline status
        setupEventListeners: function() {
            window.addEventListener('online', () => {
                this.handleOnline();
            });

            window.addEventListener('offline', () => {
                this.handleOffline();
            });

            // Handle visibility change for better UX
            document.addEventListener('visibilitychange', () => {
                if (!document.hidden && navigator.onLine) {
                    this.syncOfflineData();
                }
            });
        },

        // Handle coming back online
        handleOnline: function() {
            console.log('🔗 Back online');
            this.isOnline = true;
            this.updateOnlineStatus();

            // Show online notification
            this.showNotification('Connected', 'You are back online', 'success');

            // Sync offline data
            this.syncOfflineData();

            // Dispatch custom event
            window.dispatchEvent(new CustomEvent('backOnline'));
        },

        // Handle going offline
        handleOffline: function() {
            console.log('📡 Gone offline');
            this.isOnline = false;
            this.updateOnlineStatus();

            // Show offline notification
            this.showNotification('Offline', 'You are currently offline. Some features may be limited.', 'warning');

            // Dispatch custom event
            window.dispatchEvent(new CustomEvent('goneOffline'));
        },

        // Update UI based on online status
        updateOnlineStatus: function() {
            const body = document.body;

            if (this.isOnline) {
                body.classList.remove('offline-mode');
                body.classList.add('online-mode');
            } else {
                body.classList.remove('online-mode');
                body.classList.add('offline-mode');
            }

            // Update status indicator if present
            const statusIndicator = document.querySelector('.connection-status .status-indicator');
            if (statusIndicator) {
                statusIndicator.className = this.isOnline ? 'status-indicator online' : 'status-indicator';
            }
        },

        // Initialize service worker
        initializeServiceWorker: function() {
            if (!this.hasServiceWorker) {
                console.warn('Service Worker not supported');
                return;
            }

            navigator.serviceWorker.register('/sw.js')
                .then(registration => {
                    console.log('Service Worker registered successfully');

                    // Listen for updates
                    registration.addEventListener('updatefound', () => {
                        const newWorker = registration.installing;
                        newWorker.addEventListener('statechange', () => {
                            if (newWorker.state === 'installed' && navigator.serviceWorker.controller) {
                                this.showNotification('Update Available', 'A new version is available. Refresh to update.', 'info');
                            }
                        });
                    });
                })
                .catch(error => {
                    console.error('Service Worker registration failed:', error);
                });
        },

        // Set up background sync
        setupBackgroundSync: function() {
            if (!this.hasBackgroundSync) {
                console.warn('Background Sync not supported');
                return;
            }

            navigator.serviceWorker.ready.then(registration => {
                return registration.sync.register('background-sync');
            }).catch(error => {
                console.error('Background sync registration failed:', error);
            });
        },

        // Set up push notifications
        setupPushNotifications: function() {
            if (!this.hasPushNotifications) {
                console.warn('Push notifications not supported');
                return;
            }

            // Push notification setup would go here
        },

        // Sync offline data when back online
        syncOfflineData: function() {
            if (!this.isOnline) return;

            console.log('🔄 Syncing offline data...');

            // Get offline actions from local storage or IndexedDB
            this.getOfflineActions().then(actions => {
                return Promise.all(actions.map(action => this.syncAction(action)));
            }).then(() => {
                console.log('✅ Offline data synced successfully');
                this.showNotification('Synced', 'Offline data has been synchronized', 'success');
            }).catch(error => {
                console.error('❌ Failed to sync offline data:', error);
            });
        },

        // Get offline actions (mock implementation)
        getOfflineActions: function() {
            return Promise.resolve([]);
        },

        // Sync individual action (mock implementation)
        syncAction: function(action) {
            console.log('Syncing action:', action);
            return Promise.resolve();
        },

        // Show user notification
        showNotification: function(title, message, type = 'info') {
            // Create notification element
            const notification = document.createElement('div');
            notification.className = `notification notification-${type}`;
            notification.innerHTML = `
                <div class=""notification-content"">
                    <strong>${title}</strong>
                    <p>${message}</p>
                </div>
                <button class=""notification-close"" onclick=""this.parentNode.remove()"">&times;</button>
            `;

            // Add to page
            const container = document.querySelector('.notification-container') || this.createNotificationContainer();
            container.appendChild(notification);

            // Auto-remove after 5 seconds
            setTimeout(() => {
                if (notification.parentNode) {
                    notification.remove();
                }
            }, 5000);
        },

        // Create notification container if it doesn't exist
        createNotificationContainer: function() {
            const container = document.createElement('div');
            container.className = 'notification-container';
            container.style.cssText = `
                position: fixed;
                top: 20px;
                right: 20px;
                z-index: 1000;
                max-width: 400px;
            `;

            document.body.appendChild(container);
            return container;
        },

        // Queue action for offline execution
        queueOfflineAction: function(action) {
            const actions = this.getStoredOfflineActions();
            actions.push({
                id: Date.now().toString(),
                action: action,
                timestamp: new Date().toISOString()
            });

            localStorage.setItem('offlineActions', JSON.stringify(actions));
        },

        // Get stored offline actions
        getStoredOfflineActions: function() {
            const stored = localStorage.getItem('offlineActions');
            return stored ? JSON.parse(stored) : [];
        },

        // Clear offline actions after sync
        clearOfflineActions: function() {
            localStorage.removeItem('offlineActions');
        }
    };

    // Initialize when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => {
            window.OfflineManager.init();
        });
    } else {
        window.OfflineManager.init();
    }

    // Global offline detection functions
    window.isOnline = function() {
        return navigator.onLine;
    };

    window.addOfflineListener = function(callback) {
        window.addEventListener('online', callback);
        window.addEventListener('offline', callback);
    };

    window.queueForOffline = function(action) {
        if (!navigator.onLine) {
            window.OfflineManager.queueOfflineAction(action);
        }
    };

})();
</script>

<style>
/* Offline-related styles */
.offline-mode {
    filter: grayscale(0.3) opacity(0.8);
    position: relative;
}

.offline-mode::before {
    content: 'Offline Mode';
    position: fixed;
    top: 10px;
    left: 50%;
    transform: translateX(-50%);
    background: rgba(255, 107, 107, 0.9);
    color: white;
    padding: 8px 16px;
    border-radius: 20px;
    font-size: 0.9rem;
    font-weight: 600;
    z-index: 9999;
    backdrop-filter: blur(10px);
}

.online-mode::before {
    display: none;
}

/* Notification styles */
.notification {
    background: white;
    border-radius: 8px;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
    margin-bottom: 1rem;
    padding: 1rem;
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    max-width: 100%;
    animation: slideIn 0.3s ease;
}

.notification-success {
    border-left: 4px solid #51cf66;
}

.notification-warning {
    border-left: 4px solid #ffd43b;
}

.notification-error {
    border-left: 4px solid #ff6b6b;
}

.notification-info {
    border-left: 4px solid #339af0;
}

.notification-close {
    background: none;
    border: none;
    font-size: 1.5rem;
    cursor: pointer;
    color: #999;
    margin-left: 1rem;
    padding: 0;
    line-height: 1;
}

.notification-close:hover {
    color: #333;
}

@keyframes slideIn {
    from {
        transform: translateX(100%);
        opacity: 0;
    }
    to {
        transform: translateX(0);
        opacity: 1;
    }
}

/* Mobile responsive */
@media (max-width: 768px) {
    .notification-container {
        left: 10px;
        right: 10px;
        max-width: none;
    }

    .offline-mode::before {
        font-size: 0.8rem;
        padding: 6px 12px;
    }
}
</style>";
    }

    public string GenerateSyncStrategyScript()
    {
        return @"// Sync Strategy Implementation
(function() {
    'use strict';

    // Sync strategies for different data types
    window.SyncManager = {
        strategies: {
            // Network-first: Always try network first, cache as fallback
            networkFirst: async function(request) {
                try {
                    const response = await fetch(request);
                    if (response.ok) {
                        // Cache successful responses
                        const cache = await caches.open('potion-sync-cache');
                        cache.put(request, response.clone());
                        return response;
                    }
                    throw new Error('Network response not ok');
                } catch (error) {
                    // Network failed, try cache
                    const cachedResponse = await caches.match(request);
                    if (cachedResponse) {
                        return cachedResponse;
                    }
                    throw error;
                }
            },

            // Cache-first: Check cache first, network as fallback
            cacheFirst: async function(request) {
                // Try cache first
                const cachedResponse = await caches.match(request);
                if (cachedResponse) {
                    return cachedResponse;
                }

                // Not in cache, try network
                try {
                    const response = await fetch(request);
                    if (response.ok) {
                        const cache = await caches.open('potion-sync-cache');
                        cache.put(request, response.clone());
                        return response;
                    }
                    throw new Error('Network response not ok');
                } catch (error) {
                    throw new Error('Resource not available offline');
                }
            },

            // Stale-while-revalidate: Return cache immediately, update in background
            staleWhileRevalidate: async function(request) {
                const cache = await caches.open('potion-sync-cache');
                const cachedResponse = await cache.match(request);

                // Background fetch to update cache
                const fetchPromise = fetch(request).then(response => {
                    if (response.ok) {
                        cache.put(request, response.clone());
                    }
                    return response;
                }).catch(() => null);

                // Return cached version immediately if available
                if (cachedResponse) {
                    return cachedResponse;
                }

                // No cache, wait for network
                return fetchPromise;
            }
        },

        // Initialize sync management
        init: function() {
            this.setupFetchInterception();
            this.setupFormInterception();
            this.setupOfflineQueue();
        },

        // Intercept fetch requests for offline handling
        setupFetchInterception: function() {
            const originalFetch = window.fetch;

            window.fetch = async function(request, options = {}) {
                const url = typeof request === 'string' ? request : request.url;

                // Determine sync strategy based on URL
                if (url.includes('/api/user') || url.includes('/api/preferences')) {
                    return SyncManager.strategies.networkFirst(request);
                } else if (url.includes('/css/') || url.includes('/js/') || url.includes('/images/')) {
                    return SyncManager.strategies.cacheFirst(request);
                } else {
                    return SyncManager.strategies.staleWhileRevalidate(request);
                }
            };
        },

        // Intercept form submissions for offline queuing
        setupFormInterception: function() {
            document.addEventListener('submit', async (event) => {
                const form = event.target;
                if (!form.matches('form[data-offline-enabled]')) return;

                if (!navigator.onLine) {
                    event.preventDefault();
                    await this.queueFormSubmission(form);
                }
            });
        },

        // Set up offline action queue
        setupOfflineQueue: function() {
            // Create IndexedDB for offline actions if supported
            if ('indexedDB' in window) {
                this.initOfflineDatabase();
            } else {
                // Fallback to localStorage
                this.offlineActions = JSON.parse(localStorage.getItem('offlineActions') || '[]');
            }
        },

        // Initialize IndexedDB for offline actions
        initOfflineDatabase: function() {
            const request = indexedDB.open('PotionOfflineDB', 1);

            request.onerror = () => {
                console.error('IndexedDB not available, using localStorage');
                this.offlineActions = JSON.parse(localStorage.getItem('offlineActions') || '[]');
            };

            request.onsuccess = (event) => {
                this.db = event.target.result;
                this.loadOfflineActions();
            };

            request.onupgradeneeded = (event) => {
                const db = event.target.result;
                if (!db.objectStoreNames.contains('actions')) {
                    const store = db.createObjectStore('actions', { keyPath: 'id', autoIncrement: true });
                    store.createIndex('timestamp', 'timestamp', { unique: false });
                }
            };
        },

        // Load offline actions from storage
        loadOfflineActions: function() {
            if (this.db) {
                const transaction = this.db.transaction(['actions'], 'readonly');
                const store = transaction.objectStore('actions');
                const request = store.getAll();

                request.onsuccess = () => {
                    this.offlineActions = request.result;
                };
            }
        },

        // Queue form submission for offline sync
        queueFormSubmission: async function(form) {
            const formData = new FormData(form);
            const action = {
                type: 'form_submission',
                url: form.action,
                method: form.method,
                data: Object.fromEntries(formData.entries()),
                timestamp: new Date().toISOString()
            };

            await this.queueAction(action);

            // Show offline success message
            this.showOfflineSuccess('Form saved offline and will be submitted when back online');
        },

        // Queue any action for offline sync
        queueAction: async function(action) {
            action.id = Date.now().toString();
            action.timestamp = new Date().toISOString();

            if (this.db) {
                // Store in IndexedDB
                const transaction = this.db.transaction(['actions'], 'readwrite');
                const store = transaction.objectStore('actions');
                store.add(action);
            } else {
                // Store in localStorage
                this.offlineActions.push(action);
                localStorage.setItem('offlineActions', JSON.stringify(this.offlineActions));
            }

            console.log('Queued offline action:', action);
        },

        // Sync offline actions when back online
        syncOfflineActions: async function() {
            if (!navigator.onLine) return;

            console.log('Starting offline action sync...');

            let actionsToSync = [];

            if (this.db) {
                // Get actions from IndexedDB
                actionsToSync = await this.getActionsFromIndexedDB();
            } else {
                // Get actions from localStorage
                actionsToSync = [...this.offlineActions];
                this.offlineActions = [];
                localStorage.removeItem('offlineActions');
            }

            for (const action of actionsToSync) {
                try {
                    await this.syncAction(action);
                    await this.removeAction(action.id);
                    console.log('Synced offline action:', action.id);
                } catch (error) {
                    console.error('Failed to sync action:', action.id, error);
                }
            }

            if (actionsToSync.length > 0) {
                this.showOfflineSuccess(`${actionsToSync.length} offline actions synced successfully`);
            }
        },

        // Sync individual action
        syncAction: async function(action) {
            switch (action.type) {
                case 'form_submission':
                    return this.syncFormSubmission(action);
                case 'api_call':
                    return this.syncApiCall(action);
                default:
                    throw new Error(`Unknown action type: ${action.type}`);
            }
        },

        // Sync form submission
        syncFormSubmission: async function(action) {
            const response = await fetch(action.url, {
                method: action.method,
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                },
                body: new URLSearchParams(action.data)
            });

            if (!response.ok) {
                throw new Error(`Form submission failed: ${response.status}`);
            }

            return response;
        },

        // Sync API call
        syncApiCall: async function(action) {
            const response = await fetch(action.url, {
                method: action.method,
                headers: action.headers || {},
                body: action.body ? JSON.stringify(action.body) : undefined
            });

            if (!response.ok) {
                throw new Error(`API call failed: ${response.status}`);
            }

            return response;
        },

        // Get actions from IndexedDB
        getActionsFromIndexedDB: function() {
            return new Promise((resolve, reject) => {
                if (!this.db) {
                    resolve([]);
                    return;
                }

                const transaction = this.db.transaction(['actions'], 'readonly');
                const store = transaction.objectStore('actions');
                const request = store.getAll();

                request.onsuccess = () => resolve(request.result);
                request.onerror = () => reject(request.error);
            });
        },

        // Remove synced action
        removeAction: async function(actionId) {
            if (this.db) {
                const transaction = this.db.transaction(['actions'], 'readwrite');
                const store = transaction.objectStore('actions');
                store.delete(parseInt(actionId));
            } else {
                this.offlineActions = this.offlineActions.filter(action => action.id !== actionId);
                localStorage.setItem('offlineActions', JSON.stringify(this.offlineActions));
            }
        },

        // Show offline success message
        showOfflineSuccess: function(message) {
            const notification = document.createElement('div');
            notification.className = 'notification notification-success';
            notification.innerHTML = `
                <div class=""notification-content"">
                    <strong>Offline Sync Complete</strong>
                    <p>${message}</p>
                </div>
            `;

            const container = document.querySelector('.notification-container') ||
                (() => {
                    const div = document.createElement('div');
                    div.className = 'notification-container';
                    div.style.cssText = 'position: fixed; top: 20px; right: 20px; z-index: 1000;';
                    document.body.appendChild(div);
                    return div;
                })();

            container.appendChild(notification);

            setTimeout(() => {
                if (notification.parentNode) {
                    notification.remove();
                }
            }, 3000);
        }
    };

    // Auto-sync when back online
    window.addEventListener('online', () => {
        setTimeout(() => {
            window.SyncManager.syncOfflineActions();
        }, 1000);
    });

    // Initialize sync manager
    window.SyncManager.init();

})();
</script>";
    }

    public async Task<OfflineStatus> GetOfflineStatusAsync(HttpRequest request)
    {
        var status = new OfflineStatus
        {
            IsOnline = request.HttpContext.Connection.RemoteIpAddress != null, // Simplified check
            HasServiceWorker = true, // Assume service worker is available
            HasCacheManifest = true,
            CacheSize = 0,
            CachedResources = 0,
            OfflineCapabilities = new List<string> { "ServiceWorker", "CacheAPI", "BackgroundSync" }
        };

        return status;
    }
}
