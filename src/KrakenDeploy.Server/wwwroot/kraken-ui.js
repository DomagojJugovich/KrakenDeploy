// Small front-end shims for upstream component quirks. Pure DOM, no Blazor
// interop — safe to load once on every page.

// Client-side text-file download (project export etc.) — invoked from Blazor
// via IJSRuntime; avoids a dedicated download endpoint.
window.krakenDownload = function (fileName, text) {
    var blob = new Blob([text], { type: 'application/json' });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 10000);
};

// ── RadzenDataGrid drag-to-group: mouse drop fallback ───────────────────────
// Upstream wires the MOUSE drop solely to a Blazor @onmouseup on
// .rz-group-header; touch gets a JS elementFromPoint fallback, mouse does not
// (Radzen.Blazor.js startColumnReorder). When that mouseup doesn't reach the
// Blazor handler, the pending drag state strands inside the grid and fires on
// the NEXT click that lands on the panel — grouping appears to need an "extra
// click". Mirror the touch fallback for mouse: while a column-drag ghost is
// active, a mouseup whose point is over a registered grid's group panel
// invokes the grid's official JSInvokable drop handler. Idempotent by
// upstream's own guards: if the Blazor handler already ran, the drag state is
// cleared / the descriptor already exists, and the extra invoke is a no-op.
window.krakenGridGroupDrop = (function () {
    var refs = [];

    document.addEventListener('mouseup', function (e) {
        // Capture phase: runs before Radzen's own document-level cleanup
        // removes the ghost, so its presence identifies an active drag.
        var ghost = document.querySelector('th[id$="visual"].rz-column-draggable');
        if (!ghost) {
            return;
        }
        // Users aim the GHOST (drawn offset from the cursor), not the cursor
        // hotspot — a release that "looks" on the panel often has its point a
        // few px outside (measured live: inPanel=false with ghost overlapping).
        // Accept the drop when the cursor is within a margin of the panel OR
        // when the ghost's rectangle overlaps it.
        var MARGIN = 28;
        var ghostRect = ghost.getBoundingClientRect();
        for (var i = 0; i < refs.length; i++) {
            var gridEl = refs[i].gridEl;
            if (!gridEl || !gridEl.isConnected) {
                continue;
            }
            var panel = gridEl.querySelector('.rz-group-header');
            if (!panel) {
                continue;
            }
            var pr = panel.getBoundingClientRect();
            var pointHit =
                e.clientX >= pr.left - MARGIN && e.clientX <= pr.right + MARGIN &&
                e.clientY >= pr.top - MARGIN && e.clientY <= pr.bottom + MARGIN;
            var ghostHit = ghostRect.width > 0 &&
                ghostRect.left < pr.right && ghostRect.right > pr.left &&
                ghostRect.top < pr.bottom && ghostRect.bottom > pr.top;
            if (pointHit || ghostHit) {
                (function (r) {
                    // Give Blazor's own @onmouseup dispatch a head start; when
                    // it handled the drop this lands as a no-op.
                    setTimeout(function () {
                        try { r.dotnetRef.invokeMethodAsync('RadzenGrid.OnColumnDropToGroup'); } catch (err) { }
                    }, 150);
                })(refs[i]);
                return;
            }
        }
    }, true);

    return {
        register: function (gridEl, dotnetRef) {
            refs.push({ gridEl: gridEl, dotnetRef: dotnetRef });
        },
        // Keyed by the grid's root element — DotNetObjectReference proxies
        // don't keep JS identity across interop calls, elements do.
        unregister: function (gridEl) {
            refs = refs.filter(function (r) { return r.gridEl !== gridEl; });
        }
    };
})();

(function () {
    // ── RadzenPivotDataGrid filter-popup double-toggle ──────────────────────
    // Upstream (RadzenPivotDataGrid.razor, still present in 10.4.x/master)
    // binds BOTH @onmousedown and @onclick on the filter button to the same
    // ToggleFilter, and popup open-state is the DOM itself
    // (Radzen.togglePopup checks style.display). Every real left click thus
    // queues TWO server toggles: open-then-close — the popup "flashes", or
    // randomly survives when the round-trips land out of order.
    //
    // Deterministic fix — own the whole interaction on these buttons only:
    //  • mousedown (capture): stop Blazor's duplicated handler AND Radzen's
    //    document-level outside-close; close any open filter popup ourselves,
    //    synchronously, through Radzen's official closePopup (same path its
    //    outside-click uses, including the .NET close notification).
    //  • click: exactly ONE ToggleFilter runs against a guaranteed-closed
    //    popup → it opens. If the click was on the SAME button whose popup we
    //    just closed, swallow it → re-click closes the popup.
    //  • right click: no popup, no browser context menu; closes an open one.
    var SEL = '.rz-pivot-data-grid button[aria-haspopup="dialog"]';
    var swallowClickFor = null;

    function filterButton(e) {
        return e.target && e.target.closest ? e.target.closest(SEL) : null;
    }

    function openFilterPopup() {
        var popups = document.querySelectorAll('[id^="pivot-filter-"]');
        for (var i = 0; i < popups.length; i++) {
            if (popups[i].style.display === 'block') {
                return popups[i];
            }
        }
        return null;
    }

    function closeViaRadzen(popup) {
        try {
            var info = ((window.Radzen && Radzen.popups) || [])
                .find(function (p) { return p.id === popup.id; });
            if (info) {
                Radzen.closePopup(info.id, info.instance, info.callback);
            } else if (window.Radzen) {
                Radzen.closePopup(popup.id);
            }
        } catch (err) {
            // Leave it open — the click's toggle will close it instead.
        }
    }

    document.addEventListener('mousedown', function (e) {
        var btn = filterButton(e);
        if (!btn) {
            return;
        }

        // Kill Blazor's @onmousedown ToggleFilter and Radzen's outside-close
        // for this event — we handle both responsibilities below.
        e.stopImmediatePropagation();

        swallowClickFor = null;
        var open = openFilterPopup();
        if (open) {
            if (btn.getAttribute('aria-controls') === open.id) {
                swallowClickFor = btn; // same button: this click means "close"
            }
            closeViaRadzen(open);
        }
    }, true);

    document.addEventListener('click', function (e) {
        var btn = filterButton(e);
        if (!btn) {
            swallowClickFor = null;
            return;
        }
        if (btn === swallowClickFor) {
            swallowClickFor = null;
            e.stopImmediatePropagation(); // re-click on same button → stay closed
            return;
        }
        swallowClickFor = null;
        // Let Blazor's @onclick run: the single ToggleFilter opens the popup.
    }, true);

    document.addEventListener('contextmenu', function (e) {
        if (filterButton(e)) {
            e.preventDefault();
        }
    }, true);
})();
