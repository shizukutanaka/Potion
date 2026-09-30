// Potion Service Dashboard - Enhanced Atlassian Design
class PotionDashboard {
    constructor() {
        this.apiBaseUrl = window.location.origin;
        this.currentSection = 'overview';
        this.refreshInterval = 30000; // 30 seconds
        this.autoRefreshTimer = null;
        this.modalStack = [];

        // Advanced features properties
        this.selectedAlerts = new Set();
        this.acknowledgedAlertIds = new Set();
        this.currentAlertFilter = 'all';
        this.currentAlertSeverities = null;
        this.currentAlertComponents = null;
        this.currentAlertTimeRange = null;
        this.currentAlertSearch = '';
        this.currentSecurityTab = 'components';
        this.currentChartRange = '24h';
        // Rolling window of real metric samples collected by the poller.
        this.chartData = [];
        // Populated by the poller — initialized so the Logs/Alerts views render
        // empty instead of throwing before the first successful fetch.
        this.alertsData = [];
        this.logsData = [];
        this.searchResults = [];
        this.currentSearchCategory = 'all';
        this.currentPage = 1;
        this.pageSize = 10;
        this.currentTimeFilter = '24h';
        this.currentLogType = 'system';
        this.sortColumn = 'timestamp';
        this.sortDirection = 'desc';
        this.contextualHelpTimeout = null;
        this.dragCounter = 0;
        this.uploadedFiles = [];

        this.init();
    }

    // Escape untrusted text before it enters innerHTML — alert/log fields are
    // server-generated from component names, exception messages and service
    // display names, any of which can contain markup.
    esc(value) {
        if (value === null || value === undefined) return '';
        return String(value).replace(/[&<>"']/g, c => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
        }[c]));
    }

    async init() {
        this.setupEventListeners();
        this.setupKeyboardNavigation();
        this.setupTooltips();
        this.setupDragAndDrop();
        this.setupAdvancedSearch();

        // Track OS theme changes while the 'auto' theme is selected.
        if (window.matchMedia) {
            window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
                if (this.theme === 'auto') {
                    this.applyTheme('auto');
                }
            });
        }

        // Restore persisted settings before starting the poller.
        const storedSettings = this.getStoredSettings();
        if (storedSettings.theme) {
            this.applyTheme(storedSettings.theme);
        }
        if (Number.isFinite(storedSettings.refreshInterval) && storedSettings.refreshInterval > 0) {
            this.refreshInterval = storedSettings.refreshInterval * 1000;
        }
        if (Number.isFinite(storedSettings.itemsPerPage) && storedSettings.itemsPerPage > 0) {
            this.pageSize = storedSettings.itemsPerPage;
        }
        if (storedSettings.compactMode === true) {
            document.body.classList.add('compact-mode');
        }
        if (storedSettings.showTooltips === false) {
            document.body.classList.add('no-tooltips');
        }
        if (storedSettings.autoRefresh !== false) {
            this.startAutoRefresh();
        }
        this.showLoadingState();
        await this.refreshAllData();
        this.hideLoadingState();
        this.showSection('overview');
        this.initializeCharts();
        this.connectSignalR();
        this.prepareNotificationPermission();
    }

    // Notification.permission can only be requested inside a user gesture.
    // Warning alerts default to browser delivery even before settings are
    // saved, so arm a one-time listener on the first interaction — otherwise
    // an unsaved visitor's warnings could never become notifications.
    prepareNotificationPermission() {
        if (!('Notification' in window) || Notification.permission !== 'default') {
            return;
        }
        const settings = this.getStoredSettings();
        if (settings.criticalAlerts !== 'browser' &&
            (settings.warningAlerts ?? 'browser') !== 'browser') {
            return;
        }
        const request = () => {
            if (Notification.permission === 'default') {
                Notification.requestPermission();
            }
        };
        document.addEventListener('pointerdown', request, { once: true });
        document.addEventListener('keydown', request, { once: true });
    }

    // Connects to the /collaboration hub for live alerts and health updates.
    // Polling stays the fallback — every push also triggers a REST refresh.
    connectSignalR() {
        if (typeof signalR === 'undefined') {
            return;
        }
        try {
            const connection = new signalR.HubConnectionBuilder()
                .withUrl('/collaboration')
                .withAutomaticReconnect()
                .build();

            connection.on('SystemHealthUpdate', () => {
                this.refreshAllData();
            });
            connection.on('Alert', (alert) => {
                const severity = alert && alert.data && alert.data.severity >= 2 ? 'error' : 'warning';
                this.showNotification((alert && alert.message) || 'System alert', severity);
                this.deliverAlert(severity, (alert && alert.message) || 'System alert');
                this.refreshAllData();
            });

            connection.start()
                .then(() => Promise.all(
                    ['cpu', 'memory', 'disk', 'anomaly', 'task'].map(t =>
                        connection.invoke('SubscribeToAlerts', t).catch(() => {}))
                ))
                .catch(() => { /* hub unreachable — polling already covers updates */ });
        } catch {
            // SignalR client init failure must not break the dashboard.
        }
    }

    setupDragAndDrop() {
        const dropZone = document.getElementById('file-upload-zone');

        // Prevent default drag behaviors
        ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, this.preventDefaults, false);
            document.body.addEventListener(eventName, this.preventDefaults, false);
        });

        // Highlight drop zone when item is dragged over it
        ['dragenter', 'dragover'].forEach(eventName => {
            dropZone.addEventListener(eventName, () => {
                dropZone.classList.add('drag-over');
                this.dragCounter++;
            }, false);
        });

        ['dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, () => {
                this.dragCounter--;
                if (this.dragCounter === 0) {
                    dropZone.classList.remove('drag-over');
                }
            }, false);
        });

        // Handle drop
        dropZone.addEventListener('drop', (e) => {
            const files = e.dataTransfer.files;
            this.handleFileUpload(files);
        }, false);
    }

    setupAdvancedSearch() {
        const searchInput = document.getElementById('advanced-search-input');
        const overlay = document.getElementById('advanced-search-overlay');

        // Keyboard shortcut to open search
        document.addEventListener('keydown', (e) => {
            if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
                e.preventDefault();
                this.showAdvancedSearch();
            }

            // Close search on escape
            if (e.key === 'Escape' && overlay.style.display === 'flex') {
                this.hideAdvancedSearch();
            }
        });

        // Search input handlers
        searchInput.addEventListener('input', (e) => {
            this.handleSearchInput(e.target.value);
        });

        searchInput.addEventListener('focus', () => {
            this.showSearchResults();
        });

        // Category switching
        document.querySelectorAll('.search-category').forEach(category => {
            category.addEventListener('click', () => {
                this.switchSearchCategory(category.dataset.category);
            });
        });

        // Close on outside click
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) {
                this.hideAdvancedSearch();
            }
        });
    }

    preventDefaults(e) {
        e.preventDefault();
        e.stopPropagation();
    }

    setupEventListeners() {
        // Modal close handlers
        document.addEventListener('click', (e) => {
            if (e.target.classList.contains('modal-overlay')) {
                this.closeTopModal();
            }
        });

        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                this.closeTopModal();
            }
        });

        // Auto-refresh toggle (could be added later)
        window.addEventListener('beforeunload', () => {
            if (this.autoRefreshTimer) {
                clearInterval(this.autoRefreshTimer);
            }
        });
    }

    setupKeyboardNavigation() {
        // Keyboard shortcuts
        document.addEventListener('keydown', (e) => {
            // Ctrl/Cmd + R for refresh
            if ((e.ctrlKey || e.metaKey) && e.key === 'r') {
                e.preventDefault();
                this.refreshAllData();
            }

            // Number keys for section navigation
            const sectionKeys = {
                '1': 'overview',
                '2': 'security',
                '3': 'performance',
                '4': 'alerts'
            };

            if (sectionKeys[e.key]) {
                e.preventDefault();
                this.showSection(sectionKeys[e.key]);
            }
        });
    }

    setupTooltips() {
        // Tooltips are handled via CSS :hover, but we can enhance with JS if needed
        document.querySelectorAll('.tooltip').forEach(tooltip => {
            tooltip.addEventListener('mouseenter', () => {
                // Could add analytics or enhanced behavior here
            });
        });
    }

    // Dim cards while the first poll runs; the card DOM must survive
    // because renderers write into elements by id.
    showLoadingState() {
        document.querySelectorAll('.metric-card').forEach(card => {
            card.classList.add('loading');
        });
    }

    hideLoadingState() {
        document.querySelectorAll('.metric-card.loading').forEach(card => {
            card.classList.remove('loading');
        });
    }


    closeModal(modalId) {
        const modal = document.getElementById(modalId);
        if (modal) {
            modal.remove();
            this.modalStack = this.modalStack.filter(id => id !== modalId);
        }
    }

    closeTopModal() {
        if (this.modalStack.length > 0) {
            const topModalId = this.modalStack[this.modalStack.length - 1];
            this.closeModal(topModalId);
        }
    }

    deliverAlert(severity, message) {
        const settings = this.getStoredSettings();
        // Form defaults apply before the user ever saves: criticalAlerts='none',
        // warningAlerts='browser' (see the checked radios in index.html).
        const pref = severity === 'error'
            ? (settings.criticalAlerts ?? 'none')
            : (settings.warningAlerts ?? 'browser');
        if (settings.soundNotifications && severity === 'error') {
            this.playAlertSound();
        }
        if (pref === 'browser' && 'Notification' in window) {
            if (Notification.permission === 'granted') {
                new Notification(severity === 'error' ? 'Critical alert' : 'Warning', { body: message });
            } else if (Notification.permission !== 'denied') {
                Notification.requestPermission();
            }
        }
    }

    playAlertSound() {
        try {
            const ctx = this._audioCtx || (this._audioCtx = new (window.AudioContext || window.webkitAudioContext)());
            const osc = ctx.createOscillator();
            const gain = ctx.createGain();
            osc.connect(gain).connect(ctx.destination);
            osc.type = 'sine';
            osc.frequency.value = 880;
            gain.gain.setValueAtTime(0.1, ctx.currentTime);
            gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.5);
            osc.start();
            osc.stop(ctx.currentTime + 0.5);
        } catch {
            // AudioContext unavailable or blocked — the notification itself is enough.
        }
    }

    showNotification(message, type = 'info', duration = 5000) {
        const notificationId = `notification-${Date.now()}`;
        const notificationHTML = `
            <div class="notification notification-${this.esc(type)}" id="${notificationId}">
                <div class="notification-content">
                    <span class="notification-message">${this.esc(message)}</span>
                    <button class="notification-close" data-action="close-notification" data-arg="${notificationId}">&times;</button>
                </div>
            </div>
        `;

        // Create notification container if it doesn't exist
        let container = document.querySelector('.notification-container');
        if (!container) {
            container = document.createElement('div');
            container.className = 'notification-container';
            document.body.appendChild(container);
        }

        container.insertAdjacentHTML('beforeend', notificationHTML);

        // Auto-remove after duration
        if (duration > 0) {
            setTimeout(() => this.closeNotification(notificationId), duration);
        }
    }

    closeNotification(notificationId) {
        const notification = document.getElementById(notificationId);
        if (notification) {
            notification.remove();
        }
    }

    showSection(sectionName) {
        // Update navigation
        document.querySelectorAll('.side-nav-link').forEach(item => {
            item.classList.remove('active');
        });
        document.querySelector(`[data-action="show-section"][data-arg="${sectionName}"]`)?.classList.add('active');

        // Update content sections
        document.querySelectorAll('.content-section').forEach(section => {
            section.classList.remove('active');
        });
        document.getElementById(`${sectionName}-section`).classList.add('active');

        // Update breadcrumbs
        this.updateBreadcrumbs(sectionName);

        this.currentSection = sectionName;
    }

    updateBreadcrumbs(sectionName) {
        const sectionNames = {
            'overview': 'Overview',
            'security': 'Security',
            'performance': 'Performance',
            'alerts': 'Alerts',
            'logs': 'Event Logs'
        };

        document.getElementById('current-section').textContent = sectionNames[sectionName] || sectionName;
    }

    updateAlertsBadge(count) {
        const badge = document.getElementById('alerts-count-side');
        if (count > 0) {
            badge.textContent = count > 99 ? '99+' : count;
            badge.style.display = 'inline-block';
        } else {
            badge.style.display = 'none';
        }
    }

    startAutoRefresh() {
        this.autoRefreshTimer = setInterval(() => {
            this.refreshAllData();
        }, this.refreshInterval);
    }

    async refreshAllData() {
        try {
            this.setConnectionStatus(true);
            await Promise.all([
                this.loadOverviewData(),
                this.loadSecurityData(),
                this.loadPerformanceData(),
                this.loadLogsData()
            ]);
            this.updateLastUpdated();
        } catch (error) {
            console.error('Failed to refresh data:', error);
            this.setConnectionStatus(false);
        }
    }



    applyTheme(theme) {
        this.theme = theme;
        const body = document.body;
        body.classList.remove('light-theme', 'dark-theme');

        if (theme === 'dark') {
            body.classList.add('dark-theme');
        } else if (theme === 'auto') {
            if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
                body.classList.add('dark-theme');
            }
        }
        // light theme is default
    }


    // Advanced Settings Modal
    openAdvancedSettingsModal() {
        const modal = document.getElementById('advanced-settings-modal');
        modal.style.display = 'flex';
        modal.classList.add('active');

        // Load current settings
        this.loadCurrentSettings();
    }

    closeAdvancedSettingsModal() {
        const modal = document.getElementById('advanced-settings-modal');
        modal.classList.remove('active');
        setTimeout(() => {
            modal.style.display = 'none';
        }, 300);
    }

    loadCurrentSettings() {
        // Load current dashboard settings (would normally come from localStorage or API)
        const settings = this.getStoredSettings();

        document.getElementById('theme-select-advanced').value = settings.theme || 'light';
        document.getElementById('language-select').value = settings.language || 'en';
        document.getElementById('refresh-interval-advanced').value = settings.refreshInterval || 30;
        document.getElementById('retention-days').value = settings.retentionDays || 30;
        document.getElementById('auto-refresh-advanced').checked = settings.autoRefresh !== false;
        document.getElementById('sound-notifications').checked = settings.soundNotifications || false;
        document.getElementById('items-per-page').value = settings.itemsPerPage || 50;
        document.getElementById('date-format').value = settings.dateFormat || 'YYYY-MM-DD';
        document.getElementById('compact-mode').checked = settings.compactMode || false;
        document.getElementById('show-tooltips').checked = settings.showTooltips !== false;

        // Set notification preferences
        if (settings.criticalAlerts) {
            document.querySelector(`input[name="criticalAlerts"][value="${settings.criticalAlerts}"]`).checked = true;
        }
        if (settings.warningAlerts) {
            document.querySelector(`input[name="warningAlerts"][value="${settings.warningAlerts}"]`).checked = true;
        }
    }

    saveAdvancedSettings() {
        const form = document.getElementById('advanced-settings-form');
        const formData = new FormData(form);

        const settings = {
            theme: formData.get('theme'),
            language: formData.get('language'),
            refreshInterval: parseInt(formData.get('refreshInterval')),
            retentionDays: parseInt(formData.get('retentionDays')),
            autoRefresh: formData.has('autoRefresh'),
            soundNotifications: formData.has('soundNotifications'),
            itemsPerPage: parseInt(formData.get('itemsPerPage')),
            dateFormat: formData.get('dateFormat'),
            compactMode: formData.has('compactMode'),
            showTooltips: formData.has('showTooltips'),
            criticalAlerts: formData.get('criticalAlerts'),
            warningAlerts: formData.get('warningAlerts')
        };

        // Save settings (would normally save to localStorage or API)
        this.saveSettings(settings);

        // Apply settings
        this.applyAdvancedSettings(settings);

        // Browser notification permission must be requested inside a user
        // gesture — the settings save click is one, a SignalR alert is not.
        if ((settings.criticalAlerts === 'browser' || settings.warningAlerts === 'browser') &&
            'Notification' in window && Notification.permission === 'default') {
            Notification.requestPermission();
        }

        this.closeAdvancedSettingsModal();
        this.showNotification('Advanced settings saved successfully', 'success');
    }

    getStoredSettings() {
        try {
            const stored = localStorage.getItem('potion-dashboard-settings');
            return stored ? JSON.parse(stored) : {};
        } catch {
            return {};
        }
    }

    saveSettings(settings) {
        try {
            localStorage.setItem('potion-dashboard-settings', JSON.stringify(settings));
        } catch (error) {
            console.warn('Failed to save settings:', error);
        }
    }

    applyAdvancedSettings(settings) {
        // Apply theme
        this.applyTheme(settings.theme);

        // Apply refresh interval
        this.refreshInterval = settings.refreshInterval * 1000;
        if (this.autoRefreshTimer) {
            clearInterval(this.autoRefreshTimer);
            if (settings.autoRefresh) {
                this.startAutoRefresh();
            }
        }

        // Apply compact mode
        document.body.classList.toggle('compact-mode', settings.compactMode);

        // Apply tooltips setting
        document.body.classList.toggle('no-tooltips', !settings.showTooltips);
    }

    resetToDefaults() {
        const defaults = {
            theme: 'light',
            language: 'en',
            refreshInterval: 30,
            retentionDays: 30,
            autoRefresh: true,
            soundNotifications: false,
            itemsPerPage: 50,
            dateFormat: 'YYYY-MM-DD',
            compactMode: false,
            showTooltips: true,
            criticalAlerts: 'none',
            warningAlerts: 'browser'
        };

        this.saveSettings(defaults);
        this.loadCurrentSettings();
        this.showNotification('Settings reset to defaults', 'info');
    }


    hideFileUpload() {
        const uploadZone = document.getElementById('file-upload-zone');
        uploadZone.style.display = 'none';
    }

    triggerFileSelect() {
        document.getElementById('file-input').click();
    }

    handleFileUpload(files) {
        this.uploadedFiles = Array.from(files);

        if (this.uploadedFiles.length > 0) {
            // The service exposes no file-upload endpoint — say so honestly
            // instead of simulating fake progress.
            this.showNotification('File upload is not supported by this service', 'warning');
            this.hideFileUpload();
        }
    }




    // Help and Documentation
    showHelp() {
        const modal = document.getElementById('help-modal');
        modal.style.display = 'flex';
        modal.classList.add('active');
    }

    closeHelpModal() {
        const modal = document.getElementById('help-modal');
        modal.classList.remove('active');
        setTimeout(() => {
            modal.style.display = 'none';
        }, 300);
    }

    showKeyboardShortcuts() {
        const modal = document.getElementById('shortcuts-modal');
        modal.style.display = 'flex';
        modal.classList.add('active');
    }

    closeShortcutsModal() {
        const modal = document.getElementById('shortcuts-modal');
        modal.classList.remove('active');
        setTimeout(() => {
            modal.style.display = 'none';
        }, 300);
    }

    showTutorial() {
        this.closeHelpModal();
        this.showNotification('Interactive tutorial starting...', 'info');

        // Simulate tutorial steps
        setTimeout(() => {
            this.showNotification('Step 1: Explore the Overview dashboard', 'info');
        }, 1000);

        setTimeout(() => {
            this.showNotification('Step 2: Check Security metrics', 'info');
        }, 3000);

        setTimeout(() => {
            this.showNotification('Step 3: Monitor Performance charts', 'info');
        }, 5000);

        setTimeout(() => {
            this.showNotification('Tutorial complete! Use Ctrl+/ for help anytime.', 'success');
        }, 7000);
    }

    // Advanced Search Functionality
    showAdvancedSearch() {
        const overlay = document.getElementById('advanced-search-overlay');
        overlay.style.display = 'flex';
        document.getElementById('advanced-search-input').focus();
    }

    hideAdvancedSearch() {
        const overlay = document.getElementById('advanced-search-overlay');
        overlay.style.display = 'none';
        document.getElementById('advanced-search-input').value = '';
        this.clearSearchResults();
    }

    showSearchResults() {
        const results = document.getElementById('search-results');
        results.style.display = 'block';
        this.handleSearchInput('');
    }

    clearSearchResults() {
        const results = document.getElementById('search-results');
        results.style.display = 'none';
        const resultsContent = document.getElementById('search-results-content');
        resultsContent.innerHTML = '';
    }

    handleSearchInput(query) {
        const clearBtn = document.querySelector('.search-clear');
        clearBtn.style.display = query ? 'block' : 'none';

        if (query.length === 0) {
            this.showRecentSearches();
            return;
        }

        if (query.length < 2) {
            this.clearSearchResults();
            return;
        }

        this.performSearch(query);
    }

    showRecentSearches() {
        const resultsContent = document.getElementById('search-results-content');
        resultsContent.innerHTML = `
            <div class="search-section">
                <h4>Recent Searches</h4>
                <div class="recent-searches">
                    <div class="search-item" data-action="quick-search" data-arg="CPU usage">
                        <i class="fas fa-history"></i> CPU usage
                    </div>
                    <div class="search-item" data-action="quick-search" data-arg="error logs">
                        <i class="fas fa-history"></i> error logs
                    </div>
                    <div class="search-item" data-action="quick-search" data-arg="security alerts">
                        <i class="fas fa-history"></i> security alerts
                    </div>
                </div>
            </div>
        `;
    }

    performSearch(query) {
        // Mock search results
        // Search across the real alerts, logs, and metrics the dashboard holds
        const results = this.generateSearchResults(query);
        this.displaySearchResults(results);
    }

    generateSearchResults(query) {
        const results = [];
        const q = query.toLowerCase();
        const include = cat => this.currentSearchCategory === 'all' || this.currentSearchCategory === cat;

        // Search the real data the dashboard already holds.
        if (include('alerts') && Array.isArray(this.alertsData)) {
            this.alertsData
                .filter(a => JSON.stringify(a).toLowerCase().includes(q))
                .forEach((a, i) => results.push({
                    id: `alert-${i}`,
                    category: 'alerts',
                    title: a.title || 'Alert',
                    subtitle: `${a.severity} · ${a.component || ''}`,
                    type: 'alerts',
                    url: '#alerts'
                }));
        }

        if (include('logs') && Array.isArray(this.logsData)) {
            this.logsData
                .filter(l => `${l.message} ${l.source}`.toLowerCase().includes(q))
                .slice(0, 10)
                .forEach((l, i) => results.push({
                    id: `log-${i}`,
                    category: 'logs',
                    title: l.message,
                    subtitle: `${l.level} · ${l.source}`,
                    type: 'logs',
                    url: '#logs'
                }));
        }

        if (include('metrics') && this.lastMetrics) {
            const m = this.lastMetrics;
            const entries = {
                'CPU usage': `${m.cpu.usagePercent.toFixed(1)}%`,
                'Memory usage': `${m.memory.usedPercent.toFixed(1)}%`,
                'Disk usage': `${m.disk.usedPercent.toFixed(1)}%`,
                'Network connections': `${m.network.activeConnections}`
            };
            Object.entries(entries)
                .filter(([name]) => name.toLowerCase().includes(q))
                .forEach(([name, value], i) => results.push({
                    id: `metric-${i}`,
                    category: 'metrics',
                    title: `${name}: ${value}`,
                    subtitle: 'Current value',
                    type: 'metrics',
                    url: '#performance'
                }));
        }

        return results;
    }

    displaySearchResults(results) {
        const resultsContent = document.getElementById('search-results-content');

        if (results.length === 0) {
            resultsContent.innerHTML = `
                <div class="search-empty">
                    <i class="fas fa-search"></i>
                    <p>No results found for your search.</p>
                </div>
            `;
            return;
        }

        const groupedResults = results.reduce((acc, result) => {
            if (!acc[result.category]) {
                acc[result.category] = [];
            }
            acc[result.category].push(result);
            return acc;
        }, {});

        let html = '';
        Object.keys(groupedResults).forEach(category => {
            html += `
                <div class="search-section">
                    <h4>${this.esc(category.charAt(0).toUpperCase() + category.slice(1))}</h4>
                    ${groupedResults[category].map(result => `
                        <div class="search-result-item" data-action="navigate-result" data-arg="${result.url}">
                            <div class="search-result-icon">
                                <i class="fas fa-${result.category === 'alerts' ? 'exclamation-triangle' : result.category === 'logs' ? 'list-alt' : 'chart-line'}"></i>
                            </div>
                            <div class="search-result-content">
                                <div class="search-result-title">${this.esc(result.title)}</div>
                                <div class="search-result-subtitle">${this.esc(result.subtitle)}</div>
                            </div>
                        </div>
                    `).join('')}
                </div>
            `;
        });

        resultsContent.innerHTML = html;
    }

    switchSearchCategory(category) {
        this.currentSearchCategory = category;

        // Update active category
        document.querySelectorAll('.search-category').forEach(cat => {
            cat.classList.remove('active');
        });
        document.querySelector(`[data-category="${category}"]`).classList.add('active');

        // Re-run current search with new category
        const query = document.getElementById('advanced-search-input').value;
        if (query) {
            this.performSearch(query);
        }
    }

    performQuickSearch(query) {
        document.getElementById('advanced-search-input').value = query;
        this.performSearch(query);
    }

    navigateToResult(url) {
        this.hideAdvancedSearch();
        const section = url.replace(/^#/, '');
        if (document.getElementById(`${section}-section`)) {
            this.showSection(section);
        }
    }

    clearAdvancedSearch() {
        document.getElementById('advanced-search-input').value = '';
        this.clearSearchResults();
    }


    // Reflect the backend reachability in the header status pill.
    setConnectionStatus(connected) {
        const indicator = document.querySelector('.status-indicator-advanced');
        if (!indicator) return;

        const dot = indicator.querySelector('.status-dot');
        const text = indicator.querySelector('.status-text');

        if (connected) {
            indicator.classList.remove('critical');
            indicator.classList.add('healthy');
            dot.className = 'status-dot healthy';
            text.textContent = 'System Healthy';
        } else {
            indicator.classList.remove('healthy');
            indicator.classList.add('critical');
            dot.className = 'status-dot critical';
            text.textContent = 'Connection Lost';
        }
    }

    getDisplayLocale() {
        const lang = this.getStoredSettings().language || 'ja';
        return { en: 'en-US', ja: 'ja-JP', zh: 'zh-CN', ko: 'ko-KR' }[lang] || 'ja-JP';
    }

    formatDateTime(value) {
        const date = new Date(value);
        switch (this.getStoredSettings().dateFormat) {
            case 'MM/DD/YYYY': return date.toLocaleString('en-US');
            case 'DD/MM/YYYY': return date.toLocaleString('en-GB');
            case 'YYYY-MM-DD': return date.toLocaleString('sv-SE');
            case 'relative': return this.relativeTime(date);
            default: return date.toLocaleString(this.getDisplayLocale());
        }
    }

    formatDate(value) {
        const date = new Date(value);
        switch (this.getStoredSettings().dateFormat) {
            case 'MM/DD/YYYY': return date.toLocaleDateString('en-US');
            case 'DD/MM/YYYY': return date.toLocaleDateString('en-GB');
            case 'YYYY-MM-DD': return date.toLocaleDateString('sv-SE');
            case 'relative': return this.relativeTime(date);
            default: return date.toLocaleDateString(this.getDisplayLocale());
        }
    }

    relativeTime(date) {
        const minutes = Math.max(0, Math.round((Date.now() - date.getTime()) / 60000));
        if (minutes < 60) return `${minutes} min ago`;
        const hours = Math.round(minutes / 60);
        if (hours < 24) return `${hours} h ago`;
        return `${Math.round(hours / 24)} d ago`;
    }

    updateLastUpdated() {
        const now = new Date();
        document.getElementById('last-updated').textContent =
            now.toLocaleTimeString(this.getDisplayLocale(), {
                hour: '2-digit',
                minute: '2-digit',
                second: '2-digit'
            });
    }

    async loadOverviewData() {
        try {
            const response = await fetch(`${this.apiBaseUrl}/api/health`);
            if (!response.ok) {
                throw new Error(`GET /api/health -> ${response.status}`);
            }
            const data = await response.json();

            this.lastMetrics = data.metrics;
            this.alertsData = data.alerts || [];
            this.recordChartSample(data.metrics);

            this.updateHealthOverview(data);
            this.updateAlertsDisplay(this.alertsData);
            this.updateServicesOverview(data.metrics.services);
            this.updateSecurityOverview(data.metrics.security);
            this.updateEventsOverview(data.metrics.windowsEvents);

        } catch (error) {
            // The badge must not keep reporting the last-known-good state
            // while the API is unreachable — surface the disconnect, then let
            // refreshAllData flip the header via its rejection path.
            console.error('Failed to load overview data:', error);
            const statusEl = document.getElementById('overall-status');
            if (statusEl) {
                statusEl.className = 'status-badge offline';
                statusEl.textContent = 'Unreachable';
            }
            throw error;
        }
    }

    // Append the latest real sample to the rolling chart window (max 24 pts).
    recordChartSample(metrics) {
        this.chartData.push({
            timestamp: new Date(),
            cpu: metrics.cpu.usagePercent,
            memory: metrics.memory.usedPercent,
            disk: metrics.disk.usedPercent,
            network: Math.min(metrics.network.bytesReceivedPerSec / 1048576, 100)
        });
        if (this.chartData.length > 24) {
            this.chartData.shift();
        }
    }

    updateHealthOverview(data) {
        // Overall status
        const statusEl = document.getElementById('overall-status');
        const alerts = data.alerts || [];
        const criticalAlerts = alerts.filter(a => a.severity === 'Critical').length;
        const warningAlerts = alerts.filter(a => a.severity === 'Warning').length;

        let status = 'healthy';
        let statusText = 'Healthy';

        if (criticalAlerts > 0) {
            status = 'critical';
            statusText = 'Critical Issues';
        } else if (warningAlerts > 0) {
            status = 'warning';
            statusText = 'Warnings';
        }

        statusEl.className = `status-badge ${status}`;
        statusEl.textContent = statusText;

        // Health score (simplified calculation)
        const score = this.calculateHealthScore(data);
        document.getElementById('health-score').textContent = score;

        // CPU, Memory, Disk usage
        this.updateUsageBar('cpu-usage', 'cpu-value', data.metrics.cpu.usagePercent, 'cpu');
        this.updateUsageBar('memory-usage', 'memory-value', data.metrics.memory.usedPercent, 'memory');
        this.updateUsageBar('disk-usage', 'disk-value', data.metrics.disk.usedPercent, 'disk');
    }

    calculateHealthScore(data) {
        let score = 100;

        // Deduct points for alerts
        const alerts = data.alerts || [];
        score -= alerts.filter(a => a.severity === 'Critical').length * 20;
        score -= alerts.filter(a => a.severity === 'Warning').length * 10;

        // Deduct points for high resource usage
        if (data.metrics.cpu.usagePercent > 80) score -= 10;
        if (data.metrics.memory.usedPercent > 85) score -= 10;
        if (data.metrics.disk.usedPercent > 90) score -= 10;

        // Deduct points for failed services
        if (data.metrics.services.failedServices > 0) score -= 15;

        return Math.max(0, score);
    }

    updateUsageBar(barId, valueId, percentage, type) {
        const bar = document.getElementById(barId);
        const value = document.getElementById(valueId);

        bar.style.width = `${percentage}%`;
        value.textContent = `${percentage.toFixed(1)}%`;

        // Update color based on usage
        bar.className = 'progress-fill';
        if (percentage > 90) {
            bar.classList.add('critical');
        } else if (percentage > 75) {
            bar.classList.add('warning');
        }
    }

    updateServicesOverview(services) {
        document.getElementById('total-services').textContent = services.totalServices;
        document.getElementById('running-services').textContent = services.runningServices;
        document.getElementById('stopped-services').textContent = services.stoppedServices;
        document.getElementById('failed-services').textContent = services.failedServices;
    }

    updateSecurityOverview(security) {
        this.updateSecurityItem('defender-status', security.windowsDefenderEnabled ? 'Enabled' : 'Disabled');
        this.updateSecurityItem('firewall-status', security.firewallEnabled ? 'Enabled' : 'Disabled');

        const lastScan = security.lastSecurityScan ?
            this.formatDate(security.lastSecurityScan) : 'Never';
        document.getElementById('last-scan').textContent = lastScan;
    }

    updateSecurityItem(elementId, status) {
        const element = document.getElementById(elementId);
        element.textContent = status;
        element.className = `status-indicator ${status.toLowerCase()}`;
    }

    updateEventsOverview(events) {
        document.getElementById('error-events').textContent = events.errorEventCount;
        document.getElementById('warning-events').textContent = events.warningEventCount;
        document.getElementById('critical-events').textContent = events.criticalEventCount;
    }

    async loadSecurityData() {
        try {
            const [summaryResponse, dashboardResponse] = await Promise.all([
                fetch(`${this.apiBaseUrl}/api/health/security/summary`),
                fetch(`${this.apiBaseUrl}/api/health/security`)
            ]);
            for (const [path, res] of [
                ['/api/health/security/summary', summaryResponse],
                ['/api/health/security', dashboardResponse]
            ]) {
                if (!res.ok) {
                    throw new Error(`GET ${path} -> ${res.status}`);
                }
            }

            const summary = await summaryResponse.json();
            const dashboard = await dashboardResponse.json();

            this.updateSecurityScore(summary.securityScore);
            this.updateSecurityComponents(dashboard);
            this.updateSecurityEvents(dashboard.securityAlerts);

        } catch (error) {
            console.error('Failed to load security data:', error);
        }
    }

    updateSecurityScore(score) {
        document.getElementById('security-score').textContent = score;
    }

    updateSecurityComponents(dashboard) {
        const container = document.getElementById('security-components');
        container.innerHTML = '';

        const components = [
            { name: 'Windows Defender', status: dashboard.defenderStatus },
            { name: 'Firewall', status: dashboard.firewallStatus },
            { name: 'Real-time Protection', status: dashboard.realTimeProtection },
            { name: 'Security Events', status: dashboard.securityCount || 0 }
        ];

        components.forEach(component => {
            const item = document.createElement('div');
            item.className = 'security-component';

            item.innerHTML = `
                <span class="component-name">${this.esc(component.name)}</span>
                <span class="component-status ${component.status === 'Enabled' || component.status === 'Active' ? 'enabled' : 'disabled'}">
                    ${this.esc(component.status)}
                </span>
            `;

            container.appendChild(item);
        });
    }

    updateSecurityEvents(alerts) {
        const container = document.getElementById('security-events');
        container.innerHTML = '';

        if (!alerts || alerts.length === 0) {
            container.innerHTML = '<div class="no-events">No recent security events</div>';
            return;
        }

        alerts.slice(0, 5).forEach(alert => {
            const eventItem = document.createElement('div');
            eventItem.className = 'event-item';

            eventItem.innerHTML = `
                <div class="event-header">
                    <span class="event-title">${this.esc(alert.message)}</span>
                    <span class="event-severity ${this.esc((alert.severity || 'info').toLowerCase())}">${this.esc(alert.severity)}</span>
                </div>
                <div class="event-time">${this.formatDateTime(alert.timestamp)}</div>
            `;

            container.appendChild(eventItem);
        });
    }

    async loadPerformanceData() {
        try {
            const response = await fetch(`${this.apiBaseUrl}/api/health/metrics`);
            if (!response.ok) {
                throw new Error(`GET /api/health/metrics -> ${response.status}`);
            }
            const metrics = await response.json();

            this.updatePerformanceMetrics(metrics);

        } catch (error) {
            console.error('Failed to load performance data:', error);
        }
    }

    updatePerformanceMetrics(metrics) {
        const container = document.getElementById('performance-metrics');

        const performanceData = [
            { name: 'CPU Usage', value: `${metrics.cpu.usagePercent.toFixed(1)}%` },
            { name: 'Memory Used', value: `${metrics.memory.usedPercent.toFixed(1)}%` },
            { name: 'Disk Used', value: `${metrics.disk.usedPercent.toFixed(1)}%` },
            { name: 'Active Processes', value: metrics.cpu.processCount },
            { name: 'Network Sent/sec', value: this.formatBytes(metrics.network.bytesSentPerSec) },
            { name: 'Network Received/sec', value: this.formatBytes(metrics.network.bytesReceivedPerSec) }
        ];

        container.innerHTML = performanceData.map(item =>
            `<div class="metric-item">
                <span class="metric-name">${this.esc(item.name)}</span>
                <span class="metric-value">${this.esc(item.value)}</span>
            </div>`
        ).join('');
    }

    async loadLogsData() {
        try {
            // Real event stream: health alerts raised by the monitor.
            const response = await fetch(`${this.apiBaseUrl}/api/health`);
            if (!response.ok) {
                throw new Error(`GET /api/health -> ${response.status}`);
            }
            const data = await response.json();
            const retentionDays = this.getStoredSettings().retentionDays;
            const cutoff = Number.isFinite(retentionDays) && retentionDays > 0
                ? Date.now() - retentionDays * 86400000
                : -Infinity;
            this.logsData = (data.alerts || []).map(a => ({
                timestamp: new Date(a.timestamp),
                level: (a.severity || 'info').toLowerCase(),
                source: 'HealthMonitor',
                eventId: a.component || '-',
                message: `${a.title}: ${a.message}`
            })).filter(log => log.timestamp.getTime() >= cutoff);
            this.renderLogsTable();
        } catch (error) {
            console.error('Failed to load logs data:', error);
        }
    }

    renderLogsTable() {
        const filteredData = this.filterAndSortLogs();
        const paginatedData = this.paginateLogs(filteredData);

        this.renderLogsTableBody(paginatedData);
        this.renderPagination(filteredData.length);
    }

    filterAndSortLogs() {
        let filtered = this.logsData.filter(log => {
            // Apply time filter
            const now = new Date();
            const logTime = new Date(log.timestamp);
            const hoursDiff = (now - logTime) / (1000 * 60 * 60);

            switch (this.currentTimeFilter) {
                case '1h': return hoursDiff <= 1;
                case '24h': return hoursDiff <= 24;
                case '7d': return hoursDiff <= 24 * 7;
                default: return true;
            }
        });

        // Apply sorting
        filtered.sort((a, b) => {
            let aVal = a[this.sortColumn];
            let bVal = b[this.sortColumn];

            if (this.sortColumn === 'timestamp') {
                aVal = new Date(aVal);
                bVal = new Date(bVal);
            }

            if (aVal < bVal) return this.sortDirection === 'asc' ? -1 : 1;
            if (aVal > bVal) return this.sortDirection === 'asc' ? 1 : -1;
            return 0;
        });

        return filtered;
    }

    paginateLogs(data) {
        const startIndex = (this.currentPage - 1) * this.pageSize;
        return data.slice(startIndex, startIndex + this.pageSize);
    }

    renderLogsTableBody(logs) {
        const tbody = document.getElementById('logs-table-body');
        tbody.innerHTML = '';

        if (logs.length === 0) {
            const emptyRow = document.createElement('tr');
            emptyRow.innerHTML = `
                <td colspan="5" class="text-center">
                    <div class="empty-state">
                        <div class="empty-state-icon"><i class="fas fa-list-alt"></i></div>
                        <div class="empty-state-title">No logs found</div>
                        <div class="empty-state-description">Try adjusting your filters or time range.</div>
                    </div>
                </td>
            `;
            tbody.appendChild(emptyRow);
            return;
        }

        logs.forEach(log => {
            const row = document.createElement('tr');
            row.innerHTML = `
                <td>${this.formatDateTime(log.timestamp)}</td>
                <td><span class="event-level ${this.esc(log.level)}">${this.esc(log.level)}</span></td>
                <td>${this.esc(log.source)}</td>
                <td>${this.esc(log.eventId)}</td>
                <td>${this.esc(log.message)}</td>
            `;
            tbody.appendChild(row);
        });
    }

    renderPagination(totalItems) {
        const totalPages = Math.ceil(totalItems / this.pageSize);
        const pagination = document.getElementById('logs-pagination');

        if (totalPages <= 1) {
            pagination.innerHTML = `<div class="pagination-info">Showing ${totalItems} logs</div>`;
            return;
        }

        const startItem = (this.currentPage - 1) * this.pageSize + 1;
        const endItem = Math.min(this.currentPage * this.pageSize, totalItems);

        let paginationHTML = `
            <div class="pagination-info">
                Showing ${startItem}-${endItem} of ${totalItems} logs
            </div>
            <div class="pagination-controls">
        `;

        // Previous button
        paginationHTML += `<button class="pagination-btn${this.currentPage === 1 ? ' disabled' : ''}" data-action="change-page" data-arg="${this.currentPage - 1}">Previous</button>`;

        // Page numbers
        const startPage = Math.max(1, this.currentPage - 2);
        const endPage = Math.min(totalPages, this.currentPage + 2);

        for (let i = startPage; i <= endPage; i++) {
            paginationHTML += `<button class="pagination-btn${i === this.currentPage ? ' active' : ''}" data-action="change-page" data-arg="${i}">${i}</button>`;
        }

        // Next button
        paginationHTML += `<button class="pagination-btn${this.currentPage === totalPages ? ' disabled' : ''}" data-action="change-page" data-arg="${this.currentPage + 1}">Next</button>`;

        paginationHTML += '</div>';
        pagination.innerHTML = paginationHTML;
    }

    changePage(page) {
        const totalPages = Math.ceil(this.filterAndSortLogs().length / this.pageSize);
        if (page >= 1 && page <= totalPages) {
            this.currentPage = page;
            this.renderLogsTable();
        }
    }

    sortTable(column) {
        if (this.sortColumn === column) {
            this.sortDirection = this.sortDirection === 'asc' ? 'desc' : 'asc';
        } else {
            this.sortColumn = column;
            this.sortDirection = 'desc';
        }

        // Update sort indicators
        document.querySelectorAll('.logs-table th').forEach(th => {
            th.classList.remove('sort-asc', 'sort-desc');
        });

        const header = document.querySelector(`.logs-table th[data-column="${column}"]`);
        if (header) {
            header.classList.add(`sort-${this.sortDirection}`);
        }

        this.currentPage = 1;
        this.renderLogsTable();
    }

    alertKey(alert) {
        return alert.alertId || `${alert.component}-${alert.timestamp}`;
    }

    updateAlertsDisplay(alerts) {
        const container = document.getElementById('alerts-container');
        if (!container) return;
        container.innerHTML = '';

        // Acknowledged alerts stay hidden until the condition clears.
        alerts = (alerts || []).filter(a => !this.acknowledgedAlertIds.has(this.alertKey(a)));

        if (!alerts || alerts.length === 0) {
            container.innerHTML = '<div class="no-alerts">No active alerts</div>';
            this.updateAlertsBadge(0);
            return;
        }

        this.updateAlertsBadge(alerts.length);

        // Apply search and filter
        const filteredAlerts = this.filterAlerts(alerts);

        if (filteredAlerts.length === 0) {
            container.innerHTML = '<div class="no-alerts">No alerts match your search criteria</div>';
            return;
        }

        filteredAlerts.forEach(alert => {
            const alertItem = document.createElement('div');
            alertItem.className = `alert-item ${alert.severity.toLowerCase()}`;
            alertItem.dataset.alertId = this.alertKey(alert);

            const isSelected = this.selectedAlerts.has(alertItem.dataset.alertId);

            alertItem.innerHTML = `
                <input type="checkbox" class="alert-checkbox" ${isSelected ? 'checked' : ''}>
                <div class="alert-header">
                    <div class="alert-title">${this.esc(alert.component)}: ${this.esc(alert.message)}</div>
                    <div class="alert-severity ${this.esc((alert.severity || 'info').toLowerCase())}">${this.esc(alert.severity)}</div>
                </div>
                <div class="alert-message">${this.esc(alert.message)}</div>
                <div class="alert-metadata">
                    <span><i class="fas fa-clock"></i> ${this.formatDateTime(alert.timestamp)}</span>
                    <span><i class="fas fa-tag"></i> ${this.esc(alert.component)}</span>
                </div>
            `;

            alertItem.querySelector('.alert-checkbox')
                .addEventListener('change', () => this.toggleAlertSelection(alertItem.dataset.alertId));

            if (isSelected) {
                alertItem.classList.add('selected');
            }

            container.appendChild(alertItem);
        });

        this.updateBulkActionsVisibility();
    }

    filterAlerts(alerts) {
        const timeRangeMs = { '1h': 3600000, '24h': 86400000, '7d': 604800000, '30d': 2592000000 }[this.currentAlertTimeRange];
        return alerts.filter(alert => {
            // Apply severity filter
            if (this.currentAlertFilter !== 'all' && alert.severity.toLowerCase() !== this.currentAlertFilter) {
                return false;
            }

            // Apply advanced severity allowlist (checkboxes)
            if (this.currentAlertSeverities && !this.currentAlertSeverities.includes(alert.severity.toLowerCase())) {
                return false;
            }

            // Apply component allowlist
            if (this.currentAlertComponents &&
                !this.currentAlertComponents.some(cf => (alert.component || '').toLowerCase().includes(cf.toLowerCase()))) {
                return false;
            }

            // Apply time-range bound
            if (timeRangeMs && Date.now() - new Date(alert.timestamp).getTime() > timeRangeMs) {
                return false;
            }

            // Apply search filter
            if (this.currentAlertSearch) {
                const searchTerm = this.currentAlertSearch.toLowerCase();
                return alert.message.toLowerCase().includes(searchTerm) ||
                       alert.component.toLowerCase().includes(searchTerm);
            }

            return true;
        });
    }

    toggleAlertSelection(alertId) {
        if (this.selectedAlerts.has(alertId)) {
            this.selectedAlerts.delete(alertId);
        } else {
            this.selectedAlerts.add(alertId);
        }

        // Update UI
        const alertItem = document.querySelector(`[data-alert-id="${alertId}"]`);
        if (alertItem) {
            alertItem.classList.toggle('selected');
            const checkbox = alertItem.querySelector('.alert-checkbox');
            checkbox.checked = this.selectedAlerts.has(alertId);
        }

        this.updateBulkActionsVisibility();
    }

    updateBulkActionsVisibility() {
        const bulkActions = document.getElementById('bulk-actions');
        const selectedCount = document.getElementById('selected-count');

        if (this.selectedAlerts.size > 0) {
            bulkActions.style.display = 'flex';
            selectedCount.textContent = `${this.selectedAlerts.size} selected`;
        } else {
            bulkActions.style.display = 'none';
        }
    }

    clearAlertSelection() {
        this.selectedAlerts.clear();

        // Update all checkboxes and UI
        document.querySelectorAll('.alert-item').forEach(item => {
            item.classList.remove('selected');
            const checkbox = item.querySelector('.alert-checkbox');
            if (checkbox) checkbox.checked = false;
        });

        this.updateBulkActionsVisibility();
    }

    toggleSelectAll() {
        const visibleAlerts = document.querySelectorAll('.alert-item:not([style*="display: none"])');
        const allSelected = visibleAlerts.length > 0 && [...visibleAlerts].every(item => item.classList.contains('selected'));

        if (allSelected) {
            // Deselect all
            visibleAlerts.forEach(item => {
                const alertId = item.dataset.alertId;
                this.selectedAlerts.delete(alertId);
                item.classList.remove('selected');
                const checkbox = item.querySelector('.alert-checkbox');
                if (checkbox) checkbox.checked = false;
            });
        } else {
            // Select all visible
            visibleAlerts.forEach(item => {
                const alertId = item.dataset.alertId;
                this.selectedAlerts.add(alertId);
                item.classList.add('selected');
                const checkbox = item.querySelector('.alert-checkbox');
                if (checkbox) checkbox.checked = true;
            });
        }

        this.updateBulkActionsVisibility();
    }

    bulkAcknowledge() {
        const count = this.selectedAlerts.size;
        this.selectedAlerts.forEach(id => this.acknowledgedAlertIds.add(id));
        this.clearAlertSelection();
        this.updateAlertsDisplay(this.alertsData);
        this.showNotification(`${count} alert${count > 1 ? 's' : ''} acknowledged`, 'success');
    }

    exportAlerts() {
        const alerts = Array.from(document.querySelectorAll('.alert-item')).map(item => ({
            component: item.querySelector('.alert-title').textContent.split(':')[0],
            message: item.querySelector('.alert-message').textContent,
            severity: item.querySelector('.alert-severity').textContent,
            timestamp: item.querySelector('.alert-metadata span:first-child').textContent
        }));

        const csvContent = 'Component,Message,Severity,Timestamp\n' +
            alerts.map(alert =>
                `"${alert.component}","${alert.message}","${alert.severity}","${alert.timestamp}"`
            ).join('\n');

        const blob = new Blob([csvContent], { type: 'text/csv;charset=utf-8;' });
        const link = document.createElement('a');
        link.href = URL.createObjectURL(blob);
        link.download = `alerts-export-${new Date().toISOString().split('T')[0]}.csv`;
        link.click();

        this.showNotification('Alerts exported successfully', 'success');
    }

    // Tab switching functionality
    switchTab(tabName) {
        // Update tab buttons
        document.querySelectorAll('.tab').forEach(tab => tab.classList.remove('active'));
        document.querySelector(`[data-action="switch-tab" data-arg="${tabName}"]`).classList.add('active');

        // Update tab content
        document.querySelectorAll('.tab-content').forEach(content => content.classList.remove('active'));
        document.getElementById(`${tabName}-tab`).classList.add('active');

        this.currentSecurityTab = tabName;

        // Load tab-specific data
        if (tabName === 'policies') {
            this.loadSecurityPolicies();
        }
    }

    loadSecurityPolicies() {
        // Render the real security state reported by /api/health/metrics
        // (from the last overview fetch); fall back to a refresh if empty.
        const sec = this.lastMetrics?.security;
        const ctx = this.lastMetrics?.securityContext;
        const policies = sec ? [
            {
                title: 'Windows Defender',
                description: 'Real-time antivirus protection',
                status: sec.windowsDefenderEnabled ? 'enabled' : 'disabled',
                lastUpdated: sec.lastSecurityScan ? this.formatDate(sec.lastSecurityScan) : '-'
            },
            {
                title: 'Windows Firewall',
                description: 'Network traffic filtering',
                status: sec.firewallEnabled ? 'enabled' : 'disabled',
                lastUpdated: '-'
            },
            {
                title: 'Secure Boot',
                description: 'Boot-time integrity verification',
                status: sec.isSecureBootEnabled ? 'enabled' : 'disabled',
                lastUpdated: '-'
            },
            {
                title: 'Active Threats',
                description: 'Threats currently flagged by Defender',
                status: sec.activeThreatCount > 0 ? 'warning' : 'enabled',
                lastUpdated: `${sec.activeThreatCount} detected`
            },
            {
                title: 'Service Context',
                description: ctx ? `${ctx.currentUser} (${ctx.isElevated ? 'elevated' : 'standard'})` : 'Unknown',
                status: ctx?.isElevated ? 'warning' : 'enabled',
                lastUpdated: '-'
            }
        ] : [];

        const container = document.getElementById('security-policies');
        container.innerHTML = '';

        policies.forEach(policy => {
            const policyCard = document.createElement('div');
            policyCard.className = 'policy-card';

            policyCard.innerHTML = `
                <div class="policy-header">
                    <div class="policy-icon ${this.esc(policy.status)}">
                        <i class="fas fa-shield-alt"></i>
                    </div>
                    <div class="policy-title">${this.esc(policy.title)}</div>
                    <div class="policy-status status-lozenge ${policy.status === 'enabled' ? 'success' : policy.status === 'warning' ? 'warning' : 'default'}">
                        ${this.esc(policy.status)}
                    </div>
                </div>
                <div class="policy-description">${this.esc(policy.description)}</div>
                <div class="policy-updated">
                    Last updated: ${this.esc(policy.lastUpdated)}
                </div>
            `;

            container.appendChild(policyCard);
        });
    }

    // Advanced Chart Functionality
    initializeCharts() {
        this.renderResourceTrendsChart();
    }

    renderResourceTrendsChart() {
        const canvas = document.getElementById('resource-trends-chart');
        if (!canvas) return;

        const ctx = canvas.getContext('2d');
        const filteredData = this.filterChartDataByRange();

        // Clear canvas
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        // Chart dimensions
        const chartWidth = canvas.width - 80;
        const chartHeight = canvas.height - 80;
        const startX = 60;
        const startY = 40;

        // Draw grid
        ctx.strokeStyle = '#E5E7EB';
        ctx.lineWidth = 1;
        ctx.beginPath();

        // Horizontal grid lines
        for (let i = 0; i <= 5; i++) {
            const y = startY + (chartHeight / 5) * i;
            ctx.moveTo(startX, y);
            ctx.lineTo(startX + chartWidth, y);
        }

        // Vertical grid lines
        for (let i = 0; i <= 6; i++) {
            const x = startX + (chartWidth / 6) * i;
            ctx.moveTo(x, startY);
            ctx.lineTo(x, startY + chartHeight);
        }
        ctx.stroke();

        // Draw axes labels
        ctx.fillStyle = '#6B778C';
        ctx.font = '12px Inter';
        ctx.textAlign = 'center';

        // Y-axis labels (0, 25, 50, 75, 100)
        for (let i = 0; i <= 4; i++) {
            const value = 100 - (i * 25);
            const y = startY + (chartHeight / 4) * i;
            ctx.fillText(value.toString(), startX - 20, y + 4);
        }

        // Draw lines for each metric
        const metrics = [
            { key: 'cpu', color: '#0052CC', label: 'CPU' },
            { key: 'memory', color: '#36B37E', label: 'Memory' },
            { key: 'disk', color: '#FFAB00', label: 'Disk' }
        ];

        metrics.forEach(metric => {
            ctx.strokeStyle = metric.color;
            ctx.lineWidth = 2;
            ctx.beginPath();

            filteredData.forEach((point, index) => {
                const x = startX + (chartWidth / (filteredData.length - 1)) * index;
                const y = startY + chartHeight - (point[metric.key] / 100) * chartHeight;

                if (index === 0) {
                    ctx.moveTo(x, y);
                } else {
                    ctx.lineTo(x, y);
                }
            });

            ctx.stroke();
        });

        // Draw legend
        const legendY = startY + chartHeight + 30;
        metrics.forEach((metric, index) => {
            const legendX = startX + (chartWidth / metrics.length) * index;

            // Color box
            ctx.fillStyle = metric.color;
            ctx.fillRect(legendX, legendY, 12, 12);

            // Label
            ctx.fillStyle = '#172B4D';
            ctx.textAlign = 'left';
            ctx.fillText(metric.label, legendX + 18, legendY + 10);
        });
    }

    filterChartDataByRange() {
        const now = new Date();
        const hoursBack = this.currentChartRange === '1h' ? 1 :
                         this.currentChartRange === '24h' ? 24 :
                         this.currentChartRange === '7d' ? 168 : 720; // 30d

        return this.chartData.filter(point => {
            const hoursDiff = (now - point.timestamp) / (1000 * 60 * 60);
            return hoursDiff <= hoursBack;
        });
    }

    // Performance Drawer Functionality
    openPerformanceDrawer() {
        const drawer = document.getElementById('performance-drawer');
        drawer.classList.add('open');

        // Populate drawer with current metrics
        this.updatePerformanceDrawer();
    }

    closePerformanceDrawer() {
        const drawer = document.getElementById('performance-drawer');
        drawer.classList.remove('open');
    }

    async updatePerformanceDrawer() {
        try {
            const response = await fetch(`${this.apiBaseUrl}/api/health/metrics`);
            if (!response.ok) {
                throw new Error(`GET /api/health/metrics -> ${response.status}`);
            }
            const metrics = await response.json();
            this.renderPerformanceDrawer(metrics);
        } catch (error) {
            console.error('Failed to load performance drawer data:', error);
        }
    }

    renderPerformanceDrawer(metrics) {
        const set = (id, value) => {
            const el = document.getElementById(id);
            if (el) el.textContent = value;
        };
        const gb = bytes => (bytes / (1024 ** 3)).toFixed(1) + ' GB';

        set('cpu-usage-detail', `${metrics.cpu.usagePercent.toFixed(1)}%`);
        // Windows does not expose Unix-style load averages; show core/process counts.
        set('cpu-load-1m', `${metrics.cpu.coreCount} cores`);
        set('cpu-load-5m', `${metrics.cpu.processCount} processes`);
        set('cpu-load-15m', '-');

        set('memory-total', gb(metrics.memory.totalBytes));
        set('memory-available', gb(metrics.memory.availableBytes));
        set('memory-used', gb(metrics.memory.usedBytes));
        set('memory-usage-percent', `${metrics.memory.usedPercent.toFixed(1)}%`);

        set('disk-read-rate', `${this.formatBytes(metrics.disk.readBytesPerSec)}/s`);
        set('disk-write-rate', `${this.formatBytes(metrics.disk.writeBytesPerSec)}/s`);
        set('disk-queue-length', '-');
        set('disk-total-size', gb(metrics.disk.totalBytes));

        set('network-sent', `${this.formatBytes(metrics.network.bytesSentPerSec)}/s`);
        set('network-received', `${this.formatBytes(metrics.network.bytesReceivedPerSec)}/s`);
        set('network-connections', `${metrics.network.activeConnections}`);
        set('network-utilization', '-');
    }

    // Advanced Search Functionality
    toggleAdvancedSearch() {
        const panel = document.getElementById('advanced-search-panel');
        panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
    }

    applyAdvancedFilters() {
        // Collect filter values
        const severityFilters = Array.from(document.querySelectorAll('input[name="severity"]:checked')).map(cb => cb.value);
        const timeRange = document.querySelector('input[name="timeRange"]:checked')?.value || '24h';
        const componentFilters = Array.from(document.querySelectorAll('input[name="component"]:checked')).map(cb => cb.value);

        // Apply filters to alerts
        this.currentAlertFilter = severityFilters.length === 1 ? severityFilters[0] : 'all';
        this.currentAlertSeverities = severityFilters.length > 0 ? severityFilters : [];
        this.currentAlertComponents = componentFilters.length > 0 ? componentFilters : [];
        this.currentAlertTimeRange = timeRange;

        this.refreshAllData();
        this.toggleAdvancedSearch();
        this.showNotification('Advanced filters applied', 'success');
    }

    clearAdvancedFilters() {
        // Reset all checkboxes and radio buttons
        document.querySelectorAll('#advanced-search-panel input[type="checkbox"]').forEach(cb => cb.checked = true);
        document.querySelector('input[name="timeRange"][value="24h"]').checked = true;

        this.applyAdvancedFilters();
    }

    formatBytes(bytes) {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    }
}

// Filter alerts functionality
function filterAlerts(severity) {
    const alerts = document.querySelectorAll('.alert-item');
    const filterButtons = document.querySelectorAll('.filter-btn');

    // Update active filter button
    filterButtons.forEach(btn => btn.classList.remove('active'));
    document.querySelector(`[data-action="filter-alerts" data-arg="${severity}"]`).classList.add('active');

    // Filter alerts
    alerts.forEach(alert => {
        if (severity === 'all' || alert.classList.contains(severity)) {
            alert.style.display = 'block';
        } else {
            alert.style.display = 'none';
        }
    });
}

// Global functions invoked by the data-action dispatcher below
function showSection(section) {
    if (window.dashboard) {
        window.dashboard.showSection(section);
    }
}

function refreshAllData() {
    if (window.dashboard) {
        window.dashboard.refreshAllData();
    }
}

function toggleCard(button) {
    const card = button.closest('.card-collapsible');
    const body = card.querySelector('.card-body');
    const icon = button.querySelector('i');

    if (body.classList.contains('collapsed')) {
        body.classList.remove('collapsed');
        icon.classList.remove('rotated');
    } else {
        body.classList.add('collapsed');
        icon.classList.add('rotated');
    }
}

function changeLogType(logType) {
    if (window.dashboard) {
        window.dashboard.currentLogType = logType;
        window.dashboard.currentPage = 1;
        window.dashboard.loadLogsData();
    }
}

function setTimeFilter(timeFilter) {
    if (window.dashboard) {
        window.dashboard.currentTimeFilter = timeFilter;
        window.dashboard.currentPage = 1;

        // Update active button
        document.querySelectorAll('.time-filter .btn').forEach(btn => {
            btn.classList.remove('active');
        });
        document.querySelector(`[data-action="set-time-filter" data-arg="${timeFilter}"]`).classList.add('active');

        window.dashboard.renderLogsTable();
    }
}

// Table sorting functionality
document.addEventListener('DOMContentLoaded', () => {
    // Add click handlers to table headers
    document.querySelectorAll('.logs-table th').forEach((header, index) => {
        const columns = ['timestamp', 'level', 'source', 'eventId', 'message'];
        header.setAttribute('data-column', columns[index]);
        header.addEventListener('click', () => {
            if (window.dashboard) {
                window.dashboard.sortTable(columns[index]);
            }
        });
    });

    // Initialize dashboard
    window.dashboard = new PotionDashboard();
});

// New global functions for enhanced features
function searchAlerts(searchTerm) {
    if (window.dashboard) {
        window.dashboard.currentAlertSearch = searchTerm;
        // Trigger re-render of alerts with new search term
        window.dashboard.refreshAllData();
    }
}

function switchTab(tabName) {
    if (window.dashboard) {
        window.dashboard.switchTab(tabName);
    }
}

function toggleSelectAll() {
    if (window.dashboard) {
        window.dashboard.toggleSelectAll();
    }
}

function clearAlertSelection() {
    if (window.dashboard) {
        window.dashboard.clearAlertSelection();
    }
}

function bulkAcknowledge() {
    if (window.dashboard) {
        window.dashboard.bulkAcknowledge();
    }
}

function exportAlerts() {
    if (window.dashboard) {
        window.dashboard.exportAlerts();
    }
}

// New global functions for advanced features
function openAdvancedSettingsModal() {
    if (window.dashboard) {
        window.dashboard.openAdvancedSettingsModal();
    }
}

function closeAdvancedSettingsModal() {
    if (window.dashboard) {
        window.dashboard.closeAdvancedSettingsModal();
    }
}

function saveAdvancedSettings() {
    if (window.dashboard) {
        window.dashboard.saveAdvancedSettings();
    }
}

function resetToDefaults() {
    if (window.dashboard) {
        window.dashboard.resetToDefaults();
    }
}

function triggerFileSelect() {
    if (window.dashboard) {
        window.dashboard.triggerFileSelect();
    }
}

function handleFileUpload(files) {
    if (window.dashboard) {
        window.dashboard.handleFileUpload(files);
    }
}

function closePerformanceDrawer() {
    if (window.dashboard) {
        window.dashboard.closePerformanceDrawer();
    }
}

function updateChartRange(range) {
    if (window.dashboard) {
        window.dashboard.currentChartRange = range;
        window.dashboard.renderResourceTrendsChart();
    }
}

function clearAdvancedFilters() {
    if (window.dashboard) {
        window.dashboard.clearAdvancedFilters();
    }
}

// Help and Documentation functions
function showHelp() {
    if (window.dashboard) {
        window.dashboard.showHelp();
    }
}

function closeHelpModal() {
    if (window.dashboard) {
        window.dashboard.closeHelpModal();
    }
}

function showKeyboardShortcuts() {
    if (window.dashboard) {
        window.dashboard.showKeyboardShortcuts();
    }
}

function closeShortcutsModal() {
    if (window.dashboard) {
        window.dashboard.closeShortcutsModal();
    }
}

function showTutorial() {
    if (window.dashboard) {
        window.dashboard.showTutorial();
    }
}

// Advanced Search functions
function showAdvancedSearch() {
    if (window.dashboard) {
        window.dashboard.showAdvancedSearch();
    }
}

function clearAdvancedSearch() {
    if (window.dashboard) {
        window.dashboard.clearAdvancedSearch();
    }
}

function openPerformanceDrawer() {
    if (window.dashboard) {
        window.dashboard.openPerformanceDrawer();
    }
}

function toggleAdvancedSearch() {
    if (window.dashboard) {
        window.dashboard.toggleAdvancedSearch();
    }
}

function applyAdvancedFilters() {
    if (window.dashboard) {
        window.dashboard.applyAdvancedFilters();
    }
}

// Delegated handlers for data-action / data-onchange / data-onkeyup attributes.
// Replaces inline on* attributes so script-src can drop 'unsafe-inline'.
const POTION_ACTIONS = {
    'show-section': (el, arg) => showSection(arg),
    'show-help': () => showHelp(),
    'show-shortcuts': () => showKeyboardShortcuts(),
    'switch-tab': (el, arg) => switchTab(arg),
    'toggle-card': (el) => toggleCard(el),
    'open-perf-drawer': () => openPerformanceDrawer(),
    'clear-alert-selection': () => clearAlertSelection(),
    'bulk-acknowledge': () => bulkAcknowledge(),
    'filter-alerts': (el, arg) => filterAlerts(arg),
    'toggle-select-all': () => toggleSelectAll(),
    'export-alerts': () => exportAlerts(),
    'set-time-filter': (el, arg) => setTimeFilter(arg),
    'toggle-advanced-search': () => toggleAdvancedSearch(),
    'close-help-modal': () => closeHelpModal(),
    'show-tutorial': () => showTutorial(),
    'close-shortcuts-modal': () => closeShortcutsModal(),
    'clear-advanced-search': () => clearAdvancedSearch(),
    'close-perf-drawer': () => closePerformanceDrawer(),
    'clear-advanced-filters': () => clearAdvancedFilters(),
    'apply-advanced-filters': () => applyAdvancedFilters(),
    'close-settings-modal': () => closeAdvancedSettingsModal(),
    'reset-defaults': () => resetToDefaults(),
    'save-adv-settings': () => saveAdvancedSettings(),
    'trigger-file-select': () => triggerFileSelect(),
    'close-modal': (el, arg) => window.dashboard?.closeModal(arg),
    'close-notification': (el, arg) => window.dashboard?.closeNotification(arg),
    'close-top-modal': () => window.dashboard?.closeTopModal(),
    'dash-save-settings': () => window.dashboard?.saveSettings(),
    'quick-search': (el, arg) => window.dashboard?.performQuickSearch(arg),
    'navigate-result': (el, arg) => window.dashboard?.navigateToResult(arg),
    'change-page': (el, arg) => window.dashboard?.changePage(parseInt(arg, 10)),
    'update-chart-range': (el) => updateChartRange(el.value),
    'search-alerts': (el) => searchAlerts(el.value),
    'change-log-type': (el) => changeLogType(el.value),
    'handle-file-upload': (el) => handleFileUpload(el.files)
};

for (const [type, attr] of [['click', 'data-action'], ['change', 'data-onchange'], ['keyup', 'data-onkeyup']]) {
    document.addEventListener(type, (event) => {
        const el = event.target instanceof Element ? event.target.closest(`[${attr}]`) : null;
        if (!el) return;
        const fn = POTION_ACTIONS[el.getAttribute(attr)];
        if (typeof fn !== 'function') return;
        fn(el, el.dataset.arg);
        if (type === 'click' && el.tagName === 'A') event.preventDefault();
    });
}
