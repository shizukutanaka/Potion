// Potion Service Dashboard - Enhanced Atlassian Design
class PotionDashboard {
    constructor() {
        this.apiBaseUrl = window.location.origin;
        this.currentSection = 'overview';
        this.refreshInterval = 30000; // 30 seconds
        this.autoRefreshTimer = null;
        this.modalStack = [];
        this.dropdownStates = new Map();

        // Advanced features properties
        this.selectedAlerts = new Set();
        this.currentAlertFilter = 'all';
        this.currentAlertSearch = '';
        this.currentSecurityTab = 'components';
        this.currentChartRange = '24h';
        this.chartData = this.generateMockChartData();
        this.searchResults = [];
        this.currentSearchCategory = 'all';
        this.contextualHelpTimeout = null;
        this.dragCounter = 0;
        this.uploadedFiles = [];
        this.inlineEditors = new Map();

        this.init();
    }

    async init() {
        this.setupEventListeners();
        this.setupKeyboardNavigation();
        this.setupDropdowns();
        this.setupTooltips();
        this.setupDragAndDrop();
        this.setupInlineEditing();
        this.setupAdvancedSearch();
        this.startAutoRefresh();
        this.showLoadingState();
        await this.refreshAllData();
        this.hideLoadingState();
        this.showSection('overview');
        this.initializeCharts();
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

    setupInlineEditing() {
        // Set up click handlers for inline editable elements
        document.addEventListener('click', (e) => {
            const inlineDisplay = e.target.closest('.inline-display');
            if (inlineDisplay && !inlineDisplay.closest('.inline-editor').classList.contains('editing')) {
                this.startInlineEditing(inlineDisplay);
            }
        });

        // Handle escape key for inline editing
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                this.cancelAllInlineEditing();
            }
        });
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
        // Navigation
        document.querySelectorAll('.dashboard-nav li').forEach(item => {
            item.addEventListener('click', () => {
                const section = item.getAttribute('onclick').match(/'([^']+)'/)[1];
                this.showSection(section);
            });
        });

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

    setupDropdowns() {
        document.addEventListener('click', (e) => {
            // Close all dropdowns when clicking outside
            if (!e.target.closest('.dropdown')) {
                this.closeAllDropdowns();
            }
        });

        // Setup dropdown toggles
        document.querySelectorAll('.dropdown-toggle').forEach(toggle => {
            toggle.addEventListener('click', (e) => {
                e.stopPropagation();
                const dropdown = toggle.closest('.dropdown');
                this.toggleDropdown(dropdown);
            });
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

    showLoadingState() {
        document.querySelectorAll('.metric-card').forEach(card => {
            const content = card.querySelector('.card-content');
            if (content) {
                content.innerHTML = `
                    <div class="loading-skeleton skeleton-card"></div>
                    <div class="loading-skeleton skeleton-text"></div>
                    <div class="loading-skeleton skeleton-text large"></div>
                `;
            }
        });
    }

    hideLoadingState() {
        // Loading state is automatically replaced by real content
    }

    toggleDropdown(dropdown) {
        const menu = dropdown.querySelector('.dropdown-menu');
        const isActive = menu.classList.contains('active');

        this.closeAllDropdowns();

        if (!isActive) {
            menu.classList.add('active');
            this.dropdownStates.set(dropdown, true);
        }
    }

    closeAllDropdowns() {
        document.querySelectorAll('.dropdown-menu.active').forEach(menu => {
            menu.classList.remove('active');
        });
        this.dropdownStates.clear();
    }

    showModal(content, options = {}) {
        const modalId = `modal-${Date.now()}`;
        const modalHTML = `
            <div class="modal-overlay active" id="${modalId}">
                <div class="modal-content">
                    <div class="modal-header">
                        <h3 class="modal-title">${options.title || 'Modal'}</h3>
                        <button class="modal-close" onclick="dashboard.closeModal('${modalId}')">&times;</button>
                    </div>
                    <div class="modal-body">
                        ${content}
                    </div>
                    ${options.footer ? `<div class="modal-footer">${options.footer}</div>` : ''}
                </div>
            </div>
        `;

        document.body.insertAdjacentHTML('beforeend', modalHTML);
        this.modalStack.push(modalId);

        // Focus management
        const modal = document.getElementById(modalId);
        const focusableElements = modal.querySelectorAll('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
        if (focusableElements.length > 0) {
            focusableElements[0].focus();
        }

        return modalId;
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

    showNotification(message, type = 'info', duration = 5000) {
        const notificationId = `notification-${Date.now()}`;
        const notificationHTML = `
            <div class="notification notification-${type}" id="${notificationId}">
                <div class="notification-content">
                    <span class="notification-message">${message}</span>
                    <button class="notification-close" onclick="dashboard.closeNotification('${notificationId}')">&times;</button>
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
        document.querySelectorAll('.dashboard-nav li').forEach(item => {
            item.classList.remove('active');
        });
        document.querySelector(`[onclick="showSection('${sectionName}')"]`).classList.add('active');

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
        const badge = document.getElementById('alerts-count');
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
                this.loadAlertsData(),
                this.loadLogsData()
            ]);
            this.updateLastUpdated();
        } catch (error) {
            console.error('Failed to refresh data:', error);
            this.setConnectionStatus(false);
        }
    }

    showSettingsModal() {
        const content = `
            <div class="form-group">
                <label class="form-label">Refresh Interval (seconds)</label>
                <input type="number" class="form-input" id="refresh-interval" value="${this.refreshInterval / 1000}" min="5" max="300">
            </div>
            <div class="form-group">
                <label class="form-label">Theme</label>
                <select class="form-input" id="theme-select">
                    <option value="light">Light</option>
                    <option value="dark">Dark</option>
                    <option value="auto">Auto</option>
                </select>
            </div>
            <div class="form-group">
                <label class="form-label">
                    <input type="checkbox" id="auto-refresh-toggle" checked> Enable Auto-refresh
                </label>
            </div>
        `;

        const footer = `
            <button class="btn btn-secondary" onclick="dashboard.closeTopModal()">Cancel</button>
            <button class="btn btn-primary" onclick="dashboard.saveSettings()">Save Settings</button>
        `;

        this.showModal(content, {
            title: 'Dashboard Settings',
            footer: footer
        });
    }

    saveSettings() {
        const refreshInterval = parseInt(document.getElementById('refresh-interval').value) * 1000;
        const theme = document.getElementById('theme-select').value;
        const autoRefresh = document.getElementById('auto-refresh-toggle').checked;

        // Update settings
        this.refreshInterval = refreshInterval;

        // Apply theme
        this.applyTheme(theme);

        // Update auto-refresh
        if (autoRefresh && !this.autoRefreshTimer) {
            this.startAutoRefresh();
        } else if (!autoRefresh && this.autoRefreshTimer) {
            clearInterval(this.autoRefreshTimer);
            this.autoRefreshTimer = null;
        }

        this.closeTopModal();
        this.showNotification('Settings saved successfully', 'success');
    }

    applyTheme(theme) {
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

    toggleAutoRefresh() {
        if (this.autoRefreshTimer) {
            clearInterval(this.autoRefreshTimer);
            this.autoRefreshTimer = null;
            this.showNotification('Auto-refresh disabled', 'info');
        } else {
            this.startAutoRefresh();
            this.showNotification('Auto-refresh enabled', 'success');
        }
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

    // File Upload Functionality
    showFileUpload() {
        const uploadZone = document.getElementById('file-upload-zone');
        uploadZone.style.display = 'flex';
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
            this.showProgressModal('Uploading Files', `Processing ${this.uploadedFiles.length} file(s)...`);

            // Simulate file upload progress
            let progress = 0;
            const interval = setInterval(() => {
                progress += Math.random() * 15;
                if (progress >= 100) {
                    progress = 100;
                    clearInterval(interval);
                    setTimeout(() => {
                        this.closeProgressModal();
                        this.showNotification(`${this.uploadedFiles.length} file(s) uploaded successfully`, 'success');
                        this.hideFileUpload();
                    }, 500);
                }
                this.updateProgress(progress, `Uploading file ${Math.floor(progress / 20) + 1} of ${this.uploadedFiles.length}...`);
            }, 200);
        }
    }

    // Progress Modal Functionality
    showProgressModal(title, initialMessage) {
        const modal = document.getElementById('progress-modal');
        document.getElementById('progress-title').textContent = title;
        document.getElementById('progress-message').textContent = initialMessage;
        this.updateProgress(0, initialMessage);
        modal.style.display = 'flex';
        modal.classList.add('active');
    }

    closeProgressModal() {
        const modal = document.getElementById('progress-modal');
        modal.classList.remove('active');
        setTimeout(() => {
            modal.style.display = 'none';
        }, 300);
    }

    updateProgress(percentage, message) {
        const fill = document.getElementById('progress-fill');
        const percentageEl = document.getElementById('progress-percentage');
        const messageEl = document.getElementById('progress-message');

        fill.style.width = `${percentage}%`;
        percentageEl.textContent = `${Math.round(percentage)}%`;
        messageEl.textContent = message;

        // Update progress stages
        const stages = [25, 50, 75, 100];
        stages.forEach((stage, index) => {
            const dot = document.querySelector(`[data-step="${index + 1}"]`);
            if (percentage >= stage) {
                dot.classList.remove('active');
                dot.classList.add('completed');
            } else if (percentage >= stage - 10) {
                dot.classList.add('active');
                dot.classList.remove('completed');
            }
        });
    }

    // Inline Editing Functionality
    startInlineEditing(displayElement) {
        const editor = displayElement.closest('.inline-editor');
        const input = editor.querySelector('.inline-input');
        const currentValue = displayElement.textContent.trim();

        editor.classList.add('editing');
        input.value = currentValue;
        input.focus();
        input.select();

        // Handle save/cancel actions
        const saveHandler = () => {
            const newValue = input.value.trim();
            if (newValue && newValue !== currentValue) {
                displayElement.textContent = newValue;
                this.showNotification('Value updated successfully', 'success');
            }
            this.endInlineEditing(editor);
        };

        const cancelHandler = () => {
            this.endInlineEditing(editor);
        };

        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                saveHandler();
            } else if (e.key === 'Escape') {
                e.preventDefault();
                cancelHandler();
            }
        });

        // Set up action buttons
        const saveBtn = editor.querySelector('.inline-actions button:first-child');
        const cancelBtn = editor.querySelector('.inline-actions button:last-child');

        if (saveBtn) saveBtn.onclick = saveHandler;
        if (cancelBtn) cancelBtn.onclick = cancelHandler;

        this.inlineEditors.set(editor, { saveHandler, cancelHandler });
    }

    endInlineEditing(editor) {
        editor.classList.remove('editing');
        const actions = this.inlineEditors.get(editor);
        if (actions) {
            // Clean up event listeners if needed
            this.inlineEditors.delete(editor);
        }
    }

    cancelAllInlineEditing() {
        document.querySelectorAll('.inline-editor.editing').forEach(editor => {
            this.endInlineEditing(editor);
        });
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
                    <div class="search-item" onclick="dashboard.performQuickSearch('CPU usage')">
                        <i class="fas fa-history"></i> CPU usage
                    </div>
                    <div class="search-item" onclick="dashboard.performQuickSearch('error logs')">
                        <i class="fas fa-history"></i> error logs
                    </div>
                    <div class="search-item" onclick="dashboard.performQuickSearch('security alerts')">
                        <i class="fas fa-history"></i> security alerts
                    </div>
                </div>
            </div>
        `;
    }

    performSearch(query) {
        // Mock search results
        const results = this.generateSearchResults(query);
        this.displaySearchResults(results);
    }

    generateSearchResults(query) {
        const results = [];
        const categories = ['alerts', 'logs', 'metrics'];

        categories.forEach(category => {
            if (this.currentSearchCategory === 'all' || this.currentSearchCategory === category) {
                for (let i = 0; i < Math.min(3, Math.floor(Math.random() * 5) + 1); i++) {
                    results.push({
                        id: `${category}-${i}`,
                        category: category,
                        title: `${query} result ${i + 1}`,
                        subtitle: `In ${category} section`,
                        type: category,
                        url: `#${category}`
                    });
                }
            }
        });

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
                    <h4>${category.charAt(0).toUpperCase() + category.slice(1)}</h4>
                    ${groupedResults[category].map(result => `
                        <div class="search-result-item" onclick="dashboard.navigateToResult('${result.url}')">
                            <div class="search-result-icon">
                                <i class="fas fa-${result.category === 'alerts' ? 'exclamation-triangle' : result.category === 'logs' ? 'list-alt' : 'chart-line'}"></i>
                            </div>
                            <div class="search-result-content">
                                <div class="search-result-title">${result.title}</div>
                                <div class="search-result-subtitle">${result.subtitle}</div>
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
        // Navigate to the result (would implement actual navigation)
        this.showNotification('Navigated to result', 'info');
    }

    clearAdvancedSearch() {
        document.getElementById('advanced-search-input').value = '';
        this.clearSearchResults();
    }

    playNotificationSound() {
        // Create a simple beep sound using Web Audio API
        try {
            const audioContext = new (window.AudioContext || window.webkitAudioContext)();
            const oscillator = audioContext.createOscillator();
            const gainNode = audioContext.createGain();

            oscillator.connect(gainNode);
            gainNode.connect(audioContext.destination);

            oscillator.frequency.setValueAtTime(800, audioContext.currentTime);
            oscillator.frequency.setValueAtTime(600, audioContext.currentTime + 0.1);

            gainNode.gain.setValueAtTime(0.3, audioContext.currentTime);
            gainNode.gain.exponentialRampToValueAtTime(0.01, audioContext.currentTime + 0.3);

            oscillator.start(audioContext.currentTime);
            oscillator.stop(audioContext.currentTime + 0.3);
        } catch (error) {
            console.warn('Could not play notification sound:', error);
        }
    }

    updateLastUpdated() {
        const now = new Date();
        document.getElementById('last-updated').textContent =
            now.toLocaleTimeString('ja-JP', {
                hour: '2-digit',
                minute: '2-digit',
                second: '2-digit'
            });
    }

    async loadOverviewData() {
        try {
            const response = await fetch(`${this.apiBaseUrl}/api/health`);
            const data = await response.json();

            this.updateHealthOverview(data);
            this.updateServicesOverview(data.metrics.services);
            this.updateSecurityOverview(data.metrics.security);
            this.updateEventsOverview(data.metrics.windowsEvents);

        } catch (error) {
            console.error('Failed to load overview data:', error);
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
            new Date(security.lastSecurityScan).toLocaleDateString('ja-JP') : 'Never';
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
                <span class="component-name">${component.name}</span>
                <span class="component-status ${component.status === 'Enabled' || component.status === 'Active' ? 'enabled' : 'disabled'}">
                    ${component.status}
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
                    <span class="event-title">${alert.message}</span>
                    <span class="event-severity ${alert.severity.toLowerCase()}">${alert.severity}</span>
                </div>
                <div class="event-time">${new Date(alert.timestamp).toLocaleString('ja-JP')}</div>
            `;

            container.appendChild(eventItem);
        });
    }

    async loadPerformanceData() {
        try {
            const response = await fetch(`${this.apiBaseUrl}/api/health/metrics`);
            const metrics = await response.json();

            this.updatePerformanceMetrics(metrics);
            this.updateResourceChart(metrics);

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
                <span class="metric-name">${item.name}</span>
                <span class="metric-value">${item.value}</span>
            </div>`
        ).join('');
    }

    updateResourceChart(metrics) {
        // Simple chart implementation - could be enhanced with Chart.js
        const canvas = document.getElementById('resource-chart');
        const ctx = canvas.getContext('2d');

        // Clear canvas
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        // Simple bar chart
        const data = [
            { label: 'CPU', value: metrics.cpu.usagePercent, color: '#0052CC' },
            { label: 'Memory', value: metrics.memory.usedPercent, color: '#36B37E' },
            { label: 'Disk', value: metrics.disk.usedPercent, color: '#FFAB00' }
        ];

        const barWidth = 60;
        const spacing = 80;
        const maxHeight = 150;

        data.forEach((item, index) => {
            const x = 50 + (index * spacing);
            const height = (item.value / 100) * maxHeight;
            const y = canvas.height - 50 - height;

            // Draw bar
            ctx.fillStyle = item.color;
            ctx.fillRect(x, y, barWidth, height);

            // Draw label
            ctx.fillStyle = '#172B4D';
            ctx.font = '12px Inter';
            ctx.textAlign = 'center';
            ctx.fillText(item.label, x + barWidth/2, canvas.height - 20);

            // Draw value
            ctx.fillText(`${item.value.toFixed(0)}%`, x + barWidth/2, y - 10);
        });
    }

    async loadLogsData() {
        try {
            // In a real implementation, this would call an API endpoint
            // For demo purposes, we'll simulate log data
            const mockLogs = this.generateMockLogs();
            this.logsData = mockLogs;
            this.renderLogsTable();
        } catch (error) {
            console.error('Failed to load logs data:', error);
        }
    }

    generateMockLogs() {
        const logs = [];
        const now = new Date();

        const sources = ['System', 'Application', 'Security'];
        const levels = ['Information', 'Warning', 'Error', 'Critical'];
        const messages = [
            'The system has recovered from a bugcheck.',
            'The Windows Defender service entered the running state.',
            'A user account was created.',
            'Windows successfully loaded the device driver.',
            'The system has started up.',
            'A process has exited.',
            'Windows Firewall service started successfully.',
            'User logon successful.',
            'System time changed.',
            'Disk cleanup completed successfully.'
        ];

        for (let i = 0; i < 150; i++) {
            const timestamp = new Date(now.getTime() - Math.random() * 24 * 60 * 60 * 1000);
            const source = sources[Math.floor(Math.random() * sources.length)];
            const level = levels[Math.floor(Math.random() * levels.length)];
            const eventId = Math.floor(Math.random() * 10000) + 1000;
            const message = messages[Math.floor(Math.random() * messages.length)];

            logs.push({
                timestamp: timestamp,
                level: level.toLowerCase(),
                source: source,
                eventId: eventId,
                message: message
            });
        }

        return logs.sort((a, b) => b.timestamp - a.timestamp);
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
                <td>${log.timestamp.toLocaleString('ja-JP')}</td>
                <td><span class="event-level ${log.level}">${log.level}</span></td>
                <td>${log.source}</td>
                <td>${log.eventId}</td>
                <td>${log.message}</td>
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
        paginationHTML += `<button class="pagination-btn${this.currentPage === 1 ? ' disabled' : ''}" onclick="dashboard.changePage(${this.currentPage - 1})">Previous</button>`;

        // Page numbers
        const startPage = Math.max(1, this.currentPage - 2);
        const endPage = Math.min(totalPages, this.currentPage + 2);

        for (let i = startPage; i <= endPage; i++) {
            paginationHTML += `<button class="pagination-btn${i === this.currentPage ? ' active' : ''}" onclick="dashboard.changePage(${i})">${i}</button>`;
        }

        // Next button
        paginationHTML += `<button class="pagination-btn${this.currentPage === totalPages ? ' disabled' : ''}" onclick="dashboard.changePage(${this.currentPage + 1})">Next</button>`;

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

    updateAlertsDisplay(alerts) {
        const container = document.getElementById('alerts-container');
        container.innerHTML = '';

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
            alertItem.dataset.alertId = alert.id || Math.random().toString(36);

            const isSelected = this.selectedAlerts.has(alertItem.dataset.alertId);

            alertItem.innerHTML = `
                <input type="checkbox" class="alert-checkbox" ${isSelected ? 'checked' : ''} onchange="dashboard.toggleAlertSelection('${alertItem.dataset.alertId}')">
                <div class="alert-header">
                    <div class="alert-title">${alert.component}: ${alert.message}</div>
                    <div class="alert-severity ${alert.severity.toLowerCase()}">${alert.severity}</div>
                </div>
                <div class="alert-message">${alert.message}</div>
                <div class="alert-metadata">
                    <span><i class="fas fa-clock"></i> ${new Date(alert.timestamp).toLocaleString('ja-JP')}</span>
                    <span><i class="fas fa-tag"></i> ${alert.component}</span>
                </div>
            `;

            if (isSelected) {
                alertItem.classList.add('selected');
            }

            container.appendChild(alertItem);
        });

        this.updateBulkActionsVisibility();
    }

    filterAlerts(alerts) {
        return alerts.filter(alert => {
            // Apply severity filter
            if (this.currentAlertFilter !== 'all' && alert.severity.toLowerCase() !== this.currentAlertFilter) {
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
        this.showNotification(`${count} alert${count > 1 ? 's' : ''} acknowledged`, 'success');
        this.clearAlertSelection();
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
        document.querySelector(`[onclick="switchTab('${tabName}')"]`).classList.add('active');

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
        // Mock security policies data
        const policies = [
            {
                title: 'Password Policy',
                description: 'Enforce strong password requirements and regular rotation',
                status: 'enabled',
                lastUpdated: '2024-01-15'
            },
            {
                title: 'Account Lockout',
                description: 'Lock accounts after failed login attempts',
                status: 'enabled',
                lastUpdated: '2024-01-10'
            },
            {
                title: 'Two-Factor Authentication',
                description: 'Require 2FA for all administrative accounts',
                status: 'warning',
                lastUpdated: '2024-01-08'
            },
            {
                title: 'Session Timeout',
                description: 'Automatically log out inactive sessions',
                status: 'enabled',
                lastUpdated: '2024-01-12'
            },
            {
                title: 'Audit Logging',
                description: 'Log all security-relevant events',
                status: 'enabled',
                lastUpdated: '2024-01-14'
            },
            {
                title: 'Network Encryption',
                description: 'Enforce encrypted connections for all network traffic',
                status: 'disabled',
                lastUpdated: '2024-01-05'
            }
        ];

        const container = document.getElementById('security-policies');
        container.innerHTML = '';

        policies.forEach(policy => {
            const policyCard = document.createElement('div');
            policyCard.className = 'policy-card';

            policyCard.innerHTML = `
                <div class="policy-header">
                    <div class="policy-icon ${policy.status}">
                        <i class="fas fa-shield-alt"></i>
                    </div>
                    <div class="policy-title">${policy.title}</div>
                    <div class="policy-status status-lozenge ${policy.status === 'enabled' ? 'success' : policy.status === 'warning' ? 'warning' : 'default'}">
                        ${policy.status}
                    </div>
                </div>
                <div class="policy-description">${policy.description}</div>
                <div style="margin-top: var(--space-2); font-size: 11px; color: var(--text-muted);">
                    Last updated: ${policy.lastUpdated}
                </div>
            `;

            container.appendChild(policyCard);
        });
    }

    // Advanced Chart Functionality
    initializeCharts() {
        this.renderResourceTrendsChart();
    }

    generateMockChartData() {
        const data = [];
        const now = new Date();

        // Generate data for the last 24 hours (24 data points)
        for (let i = 23; i >= 0; i--) {
            const timestamp = new Date(now.getTime() - i * 60 * 60 * 1000);
            data.push({
                timestamp: timestamp,
                cpu: Math.random() * 100,
                memory: 60 + Math.random() * 30,
                disk: 40 + Math.random() * 40,
                network: Math.random() * 20
            });
        }

        return data;
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

    updatePerformanceDrawer() {
        // In a real implementation, this would fetch detailed metrics
        // For demo purposes, we'll use mock data
        document.getElementById('cpu-usage-detail').textContent = '45%';
        document.getElementById('cpu-load-1m').textContent = '0.8';
        document.getElementById('cpu-load-5m').textContent = '0.6';
        document.getElementById('cpu-load-15m').textContent = '0.7';

        document.getElementById('memory-total').textContent = '16.0 GB';
        document.getElementById('memory-available').textContent = '8.5 GB';
        document.getElementById('memory-used').textContent = '7.5 GB';
        document.getElementById('memory-usage-percent').textContent = '47%';

        document.getElementById('disk-read-rate').textContent = '2.3 MB/s';
        document.getElementById('disk-write-rate').textContent = '1.8 MB/s';
        document.getElementById('disk-queue-length').textContent = '0.02';
        document.getElementById('disk-total-size').textContent = '500 GB';

        document.getElementById('network-sent').textContent = '1.2 MB/s';
        document.getElementById('network-received').textContent = '0.8 MB/s';
        document.getElementById('network-connections').textContent = '24';
        document.getElementById('network-utilization').textContent = '8%';
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
        // In a real implementation, more complex filtering would be applied

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
    document.querySelector(`[onclick="filterAlerts('${severity}')"]`).classList.add('active');

    // Filter alerts
    alerts.forEach(alert => {
        if (severity === 'all' || alert.classList.contains(severity)) {
            alert.style.display = 'block';
        } else {
            alert.style.display = 'none';
        }
    });
}

// Global functions for HTML onclick handlers
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
        document.querySelector(`[onclick="setTimeFilter('${timeFilter}')"]`).classList.add('active');

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
