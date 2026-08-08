window.krakenMonaco = {
    editors: {},
    _loadPromise: null,

    ensureLoaded: function () {
        if (window.monaco) {
            return Promise.resolve();
        }
        if (this._loadPromise) {
            return this._loadPromise;
        }
        this._loadPromise = new Promise(function (resolve, reject) {
            var script = document.createElement('script');
            script.src = 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs/loader.js';
            script.onload = function () {
                require.config({ paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs' } });
                require(['vs/editor/editor.main'], function () { resolve(); });
            };
            script.onerror = function () { reject(new Error('Failed to load Monaco editor')); };
            document.head.appendChild(script);
        });
        return this._loadPromise;
    },

    init: function (element, dotNetRef, language, value) {
        var self = this;
        return this.ensureLoaded().then(function () {
            var id = element.id;
            if (self.editors[id]) {
                self.editors[id].dispose();
            }

            var editor = monaco.editor.create(element, {
                value: value || '',
                language: language || 'powershell',
                theme: 'vs-dark',
                automaticLayout: true,
                minimap: { enabled: false },
                fontSize: 13,
                lineNumbers: 'on',
                scrollBeyondLastLine: false,
                wordWrap: 'on',
                tabSize: 4,
                padding: { top: 8 }
            });

            editor.onDidChangeModelContent(function () {
                dotNetRef.invokeMethodAsync('OnContentChanged', editor.getValue());
            });

            self.editors[id] = editor;
        });
    },

    setValue: function (elementId, value) {
        var editor = this.editors[elementId];
        if (editor) {
            editor.setValue(value || '');
        }
    },

    getValue: function (elementId) {
        var editor = this.editors[elementId];
        return editor ? editor.getValue() : '';
    },

    setLanguage: function (elementId, language) {
        var editor = this.editors[elementId];
        if (editor) {
            monaco.editor.setModelLanguage(editor.getModel(), language);
        }
    },

    dispose: function (elementId) {
        var editor = this.editors[elementId];
        if (editor) {
            editor.dispose();
            delete this.editors[elementId];
        }
    }
};
