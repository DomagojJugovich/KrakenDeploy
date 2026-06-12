// Small front-end shims for upstream component quirks. Pure DOM, no Blazor
// interop — safe to load once on every page.
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
