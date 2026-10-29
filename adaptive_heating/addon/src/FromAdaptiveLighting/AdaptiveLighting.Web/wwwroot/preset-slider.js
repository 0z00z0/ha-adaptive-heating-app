/*
	Two independent jobs for the stepped brightness/colour-temperature rail (PresetSlider.razor).

	1. The live readout. A Blazor Server @onchange only fires on release, so without this the number beside the
	   rail sits stale mid-drag. Every position's words are baked server-side into data-psl-readouts on the
	   input, so the browser can show the right one on the native `input` event with no round trip at all; the
	   server still gets the final value through the existing @onchange on release, unchanged.

	2. The brightness satellite. Press-and-hold the thumb without moving it, and a small handle appears beside it
	   that nudges the value in single 8-bit raw-brightness steps instead of jumping between named presets, and
	   names the raw value it is on. That number is written here for the same reason the readout above is: a raw
	   step is the smallest move this handle makes, and one shown a round trip late cannot say which one it was.
	   Two things gate arming, and both exist because it takes the gesture away from the coarse rail for good: the
	   press has to land on the thumb, and the rail has to carry data-psl-fine, which PresetSlider withholds while
	   the thumb is docked in the house-default pocket.
	   Not a rewrite of the rail itself: the native <input type=range> keeps working as it
	   does today for every other case, because pointerdown is never intercepted, only watched. Once the hold
	   is recognised, capture is handed from the input to the satellite element (setPointerCapture works on any
	   element while the pointer is still down, not only the one that received pointerdown), so the input simply
	   stops receiving move events for that gesture instead of fighting the satellite over the value.

	Like theme.js and lux-curve.js, neither job takes the page down if it cannot attach: a browser without
	pointer events, or a rail rendered before this script loads, just keeps the coarse-only behaviour it had
	before either feature existed.
*/
window.adaptiveLightingPresetSlider = (function () {
	'use strict';

	// ---- 1. live readout ----

	function readoutsFor(input) {
		var raw = input.getAttribute('data-psl-readouts');

		if (!raw) {
			return null;
		}

		try {
			return JSON.parse(raw);
		} catch (error) {
			return null;
		}
	}

	function applyReadout(input, readout) {
		var host = input.closest('.psl');

		if (!host) {
			return;
		}

		var borrowGroup = host.querySelector('.psl-borrow-group');
		var valueGroup = host.querySelector('.psl-value-group');
		var custom = host.querySelector('.psl-custom');

		if (borrowGroup) {
			borrowGroup.hidden = !readout.d;
		}

		if (valueGroup) {
			valueGroup.hidden = !!readout.d;
		}

		var target = host.querySelector(readout.d ? '.psl-borrowed' : '.psl-value');

		if (target) {
			target.textContent = readout.t;
		}

		if (custom) {
			custom.hidden = !readout.c;
		}

		host.classList.toggle('psl-default', !!readout.d);
	}

	// Delegated on document, not attached per rail: a Blazor Server re-render can replace the input node
	// (e.g. after the coarse value commits), and delegation needs no re-attaching when that happens.
	document.addEventListener('input', function (event) {
		var input = event.target;

		if (!input || !input.classList || !input.classList.contains('psl-range')) {
			return;
		}

		var readouts = readoutsFor(input);

		if (!readouts) {
			return;
		}

		var readout = readouts[parseInt(input.value, 10)];

		if (readout) {
			applyReadout(input, readout);
		}
	}, true);

	// ---- 2. brightness satellite ----

	var HOLD_MS = 450;
	var MOVE_TOLERANCE_PX = 6;

	// Every 3px of satellite drag is one raw step; a chosen feel, not a derived number, adjustable here
	// alone. Thumb width must match --psl-thumb in app.css, since a native range thumb's screen position cannot
	// otherwise be read back from the DOM.
	var PX_PER_STEP = 3;
	var THUMB_WIDTH_PX = 22;

	var watched = new WeakMap();

	function thumbCenterX(input) {
		var rect = input.getBoundingClientRect();
		var min = parseFloat(input.min) || 0;
		var max = parseFloat(input.max) || 0;
		var value = parseFloat(input.value) || 0;
		var fraction = max > min ? (value - min) / (max - min) : 0;
		var usable = Math.max(rect.width - THUMB_WIDTH_PX, 0);

		return rect.left + (THUMB_WIDTH_PX / 2) + (fraction * usable);
	}

	// The scale the satellite drives. PresetSlider renders the same ceiling into the handle's own text, so this
	// only has to agree with it while a gesture is live; RawBrightnessStep.MaxRaw is where the number is decided.
	var MAX_RAW = 255;

	function watchFine(input, satellite, owner) {
		if (!input || !satellite || !owner || watched.has(input)) {
			return;
		}

		var line = input.closest('.psl-line');
		var readout = satellite.querySelector('.psl-raw');
		var holdTimer = null;
		var waiting = false;
		var armed = false;
		var pointerId = null;
		var downX = 0;
		var downY = 0;
		var lastSteps = 0;
		var startRaw = null;
		var inFlight = false;
		var pendingSteps = 0;

		function clearHold() {
			if (holdTimer !== null) {
				clearTimeout(holdTimer);
				holdTimer = null;
			}
		}

		function flushNudge() {
			if (inFlight || pendingSteps === 0) {
				return;
			}

			var steps = pendingSteps;
			pendingSteps = 0;
			inFlight = true;

			owner.invokeMethodAsync('NudgeFine', steps)
				.catch(function () {
					// The circuit went away, or the component was disposed mid-gesture; nothing left to nudge.
				})
				.finally(function () {
					inFlight = false;
					flushNudge();
				});
		}

		function positionSatellite(clientX) {
			if (!line) {
				return;
			}

			var box = line.getBoundingClientRect();
			var x = Math.min(Math.max(clientX, box.left), box.right) - box.left;

			satellite.style.left = x + 'px';
		}

		// Written here, not waited for from the server: a raw step is the smallest thing this handle can
		// do, and a number that arrives a round trip after the finger moved cannot show which one it did.
		function showRaw(raw) {
			if (readout) {
				readout.textContent = '( ' + raw + ' / ' + MAX_RAW + ' )';
			}
		}

		function reveal() {
			armed = true;
			lastSteps = 0;
			startRaw = null;

			positionSatellite(thumbCenterX(input));
			satellite.hidden = false;

			try {
				satellite.setPointerCapture(pointerId);
			} catch (error) {
				// No capture available: the satellite still tracks the pointer while it stays over the rail.
			}

			owner.invokeMethodAsync('BeginFine')
				.then(function (raw) {
					// Not while a later gesture is running: a slow answer to a hold already released would
					// otherwise relabel the next one.
					if (armed && typeof raw === 'number') {
						startRaw = raw;
						showRaw(Math.min(MAX_RAW, Math.max(0, raw + lastSteps)));
					}
				})
				.catch(function () { });
		}

		function dismiss(id) {
			armed = false;
			waiting = false;
			startRaw = null;

			satellite.hidden = true;

			owner.invokeMethodAsync('EndFine').catch(function () { });

			try {
				satellite.releasePointerCapture(id);
			} catch (error) {
				// Already released, or never captured.
			}

			pointerId = null;
		}

		function onDown(event) {
			// Primary button (or touch) only; a right-click is a context menu, not a hold.
			if (typeof event.button === 'number' && event.button !== 0) {
				return;
			}

			if (armed || waiting) {
				return;
			}

			// The server says whether a fine handle means anything here. It does not while the rail is docked in
			// the house-default pocket: a nudge clamps to a real byte and can never mean "borrow".
			if (!input.hasAttribute('data-psl-fine')) {
				return;
			}

			// A hold only counts on the thumb, as this file's own header says. Without the test, a press anywhere
			// on the rail hands capture to the satellite after 450 ms and the coarse value stops moving for the
			// rest of the gesture, so a slow drag towards the leftmost stop never reaches it.
			if (Math.abs(event.clientX - thumbCenterX(input)) > THUMB_WIDTH_PX / 2) {
				return;
			}

			pointerId = event.pointerId;
			downX = event.clientX;
			downY = event.clientY;
			waiting = true;

			clearHold();
			holdTimer = setTimeout(function () {
				holdTimer = null;

				if (waiting) {
					reveal();
				}
			}, HOLD_MS);
		}

		// Watched on the input itself, before the hold completes: a real drag or an early release cancels the
		// timer so the rail keeps its ordinary behaviour.
		function onEarlyMove(event) {
			if (!waiting || event.pointerId !== pointerId) {
				return;
			}

			var dx = Math.abs(event.clientX - downX);
			var dy = Math.abs(event.clientY - downY);

			if (dx > MOVE_TOLERANCE_PX || dy > MOVE_TOLERANCE_PX) {
				clearHold();
				waiting = false;
			}
		}

		function onEarlyRelease(event) {
			if (event.pointerId !== pointerId) {
				return;
			}

			clearHold();
			waiting = false;
		}

		// Watched on the satellite once it has capture: the input no longer receives move/up for this pointer.
		function onFineMove(event) {
			if (!armed || event.pointerId !== pointerId) {
				return;
			}

			positionSatellite(event.clientX);

			var steps = Math.round((event.clientX - downX) / PX_PER_STEP);

			if (steps !== lastSteps) {
				pendingSteps += steps - lastSteps;
				lastSteps = steps;

				if (startRaw !== null) {
					showRaw(Math.min(MAX_RAW, Math.max(0, startRaw + steps)));
				}

				flushNudge();
			}

			event.preventDefault();
		}

		function onFineRelease(event) {
			if (event.pointerId !== pointerId) {
				return;
			}

			dismiss(event.pointerId);
		}

		function onElsewhere(event) {
			if (!armed || event.target === satellite || event.target === input) {
				return;
			}

			dismiss(pointerId);
		}

		input.addEventListener('pointerdown', onDown);
		input.addEventListener('pointermove', onEarlyMove);
		input.addEventListener('pointerup', onEarlyRelease);
		input.addEventListener('pointercancel', onEarlyRelease);
		satellite.addEventListener('pointermove', onFineMove);
		satellite.addEventListener('pointerup', onFineRelease);
		satellite.addEventListener('pointercancel', onFineRelease);
		document.addEventListener('pointerdown', onElsewhere, true);

		watched.set(input, function () {
			clearHold();
			input.removeEventListener('pointerdown', onDown);
			input.removeEventListener('pointermove', onEarlyMove);
			input.removeEventListener('pointerup', onEarlyRelease);
			input.removeEventListener('pointercancel', onEarlyRelease);
			satellite.removeEventListener('pointermove', onFineMove);
			satellite.removeEventListener('pointerup', onFineRelease);
			satellite.removeEventListener('pointercancel', onFineRelease);
			document.removeEventListener('pointerdown', onElsewhere, true);
		});
	}

	function releaseFine(input) {
		var teardown = watched.get(input);

		if (teardown) {
			teardown();
			watched.delete(input);
		}
	}

	return {
		watchFine: watchFine,
		releaseFine: releaseFine
	};
})();
