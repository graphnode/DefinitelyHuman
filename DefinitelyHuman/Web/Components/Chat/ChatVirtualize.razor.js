const instances = new Map();
let nextId = 1;

export function init(spacerBefore, spacerAfter, dotNetRef) {
    const id = nextId++;
    instances.set(id, new ChatVirtualizer(spacerBefore, spacerAfter, dotNetRef));
    return id;
}

export function refreshObservedElements(id) {
    instances.get(id)?.refreshObservedElements();
}

export function scrollToBottom(id) {
    instances.get(id)?.scrollToBottom();
}

export function isAtBottom(id) {
    return instances.get(id)?.isAtBottom() ?? true;
}

export function scrollToIndex(id, index) {
    instances.get(id)?.scrollToIndex(index);
}

export function stepAnchor(id, dir, cursor) {
    return instances.get(id)?.stepAnchor(dir, cursor) ?? 0;
}

export function dispose(id) {
    instances.get(id)?.dispose();
    instances.delete(id);
}

class ChatVirtualizer {
    constructor(spacerBefore, spacerAfter, dotNetRef) {
        this._spacerBefore = spacerBefore;
        this._spacerAfter = spacerAfter;
        this._dotNetRef = dotNetRef;
        this._disposed = false;
        this._pendingCallback = null;
        this._observedItems = new Map();
        this._pinUntil = 0;

        this._scrollContainer = findScrollContainer(spacerBefore);

        // IntersectionObserver on spacers: fires when user scrolls near unrendered items.
        this._intersectionObserver = new IntersectionObserver(
            entries => this._onSpacerIntersection(entries),
            { root: this._scrollContainer === document.documentElement ? null : this._scrollContainer }
        );
        this._intersectionObserver.observe(spacerBefore);
        this._intersectionObserver.observe(spacerAfter);

        // ResizeObserver on rendered items: tracks per-item heights.
        this._resizeObserver = new ResizeObserver(entries => this._onItemResize(entries));

        // ResizeObserver on spacers: IO doesn't re-fire for already-visible elements
        // after a resize, so reconnect the IO when a spacer height changes.
        this._spacerResizeObserver = new ResizeObserver(() => {
            if (this._disposed) return;
            this._pin();
            this._intersectionObserver.unobserve(this._spacerBefore);
            this._intersectionObserver.unobserve(this._spacerAfter);
            this._intersectionObserver.observe(this._spacerBefore);
            this._intersectionObserver.observe(this._spacerAfter);
        });
        this._spacerResizeObserver.observe(spacerBefore);
        this._spacerResizeObserver.observe(spacerAfter);

        // Tell .NET when the user leaves or returns to the bottom (drives "jump to latest").
        this._atBottom = true;
        this._scrollTarget = this._scrollContainer === document.documentElement ? window : this._scrollContainer;
        this._onScroll = () => {
            const at = this.isAtBottom();
            if (at === this._atBottom) return;
            this._atBottom = at;
            this._dotNetRef.invokeMethodAsync('OnAtBottomChanged', at);
        };
        this._scrollTarget.addEventListener('scroll', this._onScroll, { passive: true });
    }

    _itemElement(index) {
        return this._spacerBefore.parentElement.querySelector(`:scope > [data-cv-idx="${index}"]`);
    }

    scrollToIndex(index) {
        const el = this._itemElement(index);
        if (!el) return;
        // Rows around it are still at their estimated height, and the list may still be holding
        // itself at the bottom from when it opened. Hold this row in the middle instead while
        // everything is measured.
        this._pinIndex = index;
        this._pinUntil = performance.now() + 1000;
        el.scrollIntoView({ block: 'center' });
        // Restart the flash if this row is jumped to twice in a row.
        el.classList.remove('cv-flash');
        void el.offsetWidth;
        el.classList.add('cv-flash');
    }

    // Where a step in direction dir (-1 earlier, +1 later) searches from: the row last jumped
    // to while it is still on screen, otherwise the row just past the far edge of the viewport,
    // so rows already in view are searched before anything off screen.
    stepAnchor(dir, cursor) {
        const sc = this._scrollContainer;
        const top = sc === document.documentElement ? 0 : sc.getBoundingClientRect().top;
        const bottom = top + sc.clientHeight;
        const visible = el => {
            const r = el.getBoundingClientRect();
            return r.bottom > top && r.top < bottom;
        };

        const cursorEl = cursor >= 0 ? this._itemElement(cursor) : null;
        if (cursorEl && visible(cursorEl)) return cursor;

        let first = -1, last = -1;
        for (let el = this._spacerBefore.nextElementSibling;
             el && el !== this._spacerAfter;
             el = el.nextElementSibling) {
            if (!visible(el)) continue;
            const idx = parseInt(el.dataset.cvIdx);
            if (first < 0) first = idx;
            last = idx;
        }
        return dir < 0 ? last + 1 : first - 1;
    }

    _onSpacerIntersection(entries) {
        // Throttle: collapse rapid IO fires into one callback.
        if (this._pendingCallback !== null) return;
        if (!entries.some(e => e.isIntersecting)) return;

        this._pendingCallback = setTimeout(() => {
            this._pendingCallback = null;
            if (this._disposed) return;

            const sc = this._scrollContainer;
            this._dotNetRef.invokeMethodAsync('OnScroll', sc.scrollTop, sc.clientHeight);
        }, 50);
    }

    _onItemResize(entries) {
        if (this._disposed) return;

        for (const entry of entries) {
            const el = entry.target;
            const info = this._observedItems.get(el);
            if (!info) continue;

            const newHeight = entry.borderBoxSize?.[0]?.blockSize
                ?? el.getBoundingClientRect().height;

            if (info.height !== undefined && Math.abs(newHeight - info.height) < 0.5)
                continue;

            info.height = newHeight;
            this._dotNetRef.invokeMethodAsync('OnItemResized', info.index, newHeight);
        }
        this._pin();
    }

    refreshObservedElements() {
        if (this._disposed) return;

        const itemsBefore = parseInt(this._spacerBefore.dataset.itemsBefore || '0');
        const currentEls = new Set();
        let idx = itemsBefore;

        for (let el = this._spacerBefore.nextElementSibling;
             el && el !== this._spacerAfter;
             el = el.nextElementSibling) {
            currentEls.add(el);

            if (this._observedItems.has(el)) {
                this._observedItems.get(el).index = idx;
            } else {
                this._observedItems.set(el, { index: idx, height: undefined });
                this._resizeObserver.observe(el);
            }
            idx++;
        }

        for (const [el] of this._observedItems) {
            if (!currentEls.has(el)) {
                this._resizeObserver.unobserve(el);
                this._observedItems.delete(el);
            }
        }
    }

    scrollToBottom() {
        // Rows just rendered are still at their estimated height. Stay pinned for a moment
        // while they are measured, or the view ends up short of the real bottom.
        this._pinIndex = null;
        this._pinUntil = performance.now() + 1000;
        this._pin();
    }

    // For a moment after a jump, keeps the view where the jump put it: at the bottom, or centred
    // on the row jumped to.
    _pin() {
        if (!this._scrollContainer || performance.now() >= this._pinUntil) return;
        if (this._pinIndex != null)
            this._itemElement(this._pinIndex)?.scrollIntoView({ block: 'center' });
        else
            this._scrollContainer.scrollTop = this._scrollContainer.scrollHeight;
    }

    isAtBottom() {
        if (!this._scrollContainer) return true;
        const sc = this._scrollContainer;
        return sc.scrollHeight - sc.scrollTop - sc.clientHeight <= 50;
    }

    dispose() {
        this._disposed = true;
        if (this._pendingCallback !== null) clearTimeout(this._pendingCallback);
        this._scrollTarget.removeEventListener('scroll', this._onScroll);
        this._intersectionObserver?.disconnect();
        this._resizeObserver?.disconnect();
        this._spacerResizeObserver?.disconnect();
        this._observedItems.clear();
    }
}

function findScrollContainer(el) {
    let parent = el.parentElement;
    while (parent) {
        const style = getComputedStyle(parent);
        const ov = style.overflowY;
        if (ov === 'auto' || ov === 'scroll') return parent;
        parent = parent.parentElement;
    }
    return document.documentElement;
}
