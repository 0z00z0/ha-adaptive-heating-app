/*
	Served at _content/AdaptiveLighting.Lamplight/lamplight-info.js, loaded deferred from <head>.
	One bubble is open at a time: a click outside an open help bubble clicks that bubble's own (i), so the
	Info component's Toggle runs through the live connection and stays the only owner of the open state.
	Capture phase, so a click on another (i) closes the first bubble before the second opens.
*/
(function () {
	'use strict';

	if (window.__lamplightInfo) {
		return;
	}
	window.__lamplightInfo = true;

	document.addEventListener('click', function (event) {
		var target = event.target;
		var open = document.querySelectorAll('.info-i[aria-expanded="true"]');
		for (var i = 0; i < open.length; i++) {
			var button = open[i];
			var bubble = button.closest('.info');
			if (!bubble || !(target instanceof Node) || !bubble.contains(target)) {
				button.click();
				// Closed now, so a second click in the same task does not reopen it before the render lands.
				button.setAttribute('aria-expanded', 'false');
			}
		}
	}, true);
})();
