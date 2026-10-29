namespace Adaptive.Engine;

/// <summary>Home Assistant domains as an enum, for <see cref="EntityIdExtensions.DomainEnum"/>.</summary>
/// <remarks>Members are parsed from the entity id by name, so a rename changes what parses.</remarks>
public enum EntityDomain
{
	unknown,
	light,
	media_player,
	automation,
	binary_sensor,
	bodymiscale,
	button,
	camera,
	climate,
	device_tracker,
	input_boolean,
	input_select,
	input_text,
	number,
	person,
	scene,
	select,
	sensor,
	stt,
	sun,
	@switch, //switch is reserved word
	timer,
	todo,
	tts,
	update,
	wake_word,
	weather,
	zone
}
