/*
	Served at _content/AdaptiveHeating.Page/heating-theme.js, loaded from <head> WITHOUT defer or async, after
	the stylesheet. This is a Blazor Server page: the server paints it before any circuit exists, so a theme
	read from localStorage in OnAfterRenderAsync arrives after the first paint and the page visibly repaints.
	A classic script in <head> blocks the parser until it has run, so data-theme is on <html> before the body
	is parsed and there is nothing to repaint.

	The allow-list and the two defaults are not written here. They arrive on the script tag as data-themes,
	data-dark and data-light, rendered from HeatingThemes in C#, so the ids a browser may hold have one
	definition.

	Copied from the lighting engine and specialised: its own storage key, its own global. The two copies
	drift, and a fault found in either is carried across by hand.
*/
(function () {
	'use strict';

	var KEY = 'heating-theme';
	var script = document.currentScript;
	var allowed = (script && script.dataset.themes ? script.dataset.themes : '').split(' ').filter(Boolean);
	var darkDefault = script ? script.dataset.dark : null;
	var lightDefault = script ? script.dataset.light : null;
	var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: light)') : null;

	/* Storage throws instead of returning null in a browser with site data blocked, and a theme is not worth
	   taking the page down for. */
	function read() {
		try {
			return window.localStorage.getItem(KEY);
		} catch (e) {
			return null;
		}
	}

	function write(id) {
		try {
			if (id) {
				window.localStorage.setItem(KEY, id);
			} else {
				window.localStorage.removeItem(KEY);
			}
		} catch (e) {
			/* The choice holds for this page and is forgotten on the next. Better than an unhandled throw. */
		}
	}

	function deviceDefault() {
		return media && media.matches ? lightDefault : darkDefault;
	}

	/* Always paints: an id naming a theme this build no longer ships, or nothing stored at all, falls back
	   to the device's own default rather than leaving the page on the bare :root block unresolved. */
	function paint(id) {
		var resolved = id && allowed.indexOf(id) >= 0 ? id : deviceDefault();
		document.documentElement.setAttribute('data-theme', resolved);
	}

	/* Follow the device only while nothing is stored: a chosen theme never moves under a person who did not
	   ask it to, but "Follow the device" itself has to keep following after the system flips mid-session. */
	function onDeviceChange() {
		if (!read()) {
			paint(null);
		}
	}

	paint(read());

	if (media) {
		if (media.addEventListener) {
			media.addEventListener('change', onDeviceChange);
		} else if (media.addListener) {
			media.addListener(onDeviceChange);
		}
	}

	window.heatingTheme = {
		/* What the picker should show as selected once the circuit is up. Unvalidated on purpose: the server
		   resolves it, so the fallback rule lives in one place. */
		stored: read,

		/* "system" is not a data-theme value: it is the absence of a stored choice. */
		apply: function (id) {
			if (id === 'system') {
				write(null);
				paint(null);
			} else {
				write(id);
				paint(id);
			}
		}
	};
})();
