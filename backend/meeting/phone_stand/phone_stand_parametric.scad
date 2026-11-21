/*
PARAMETRIC PHONE STAND — XR/AI Hackathon Demo
Units: millimetres. One-piece stand; designed for FDM printing.

KEY AI-EDITABLE PARAMETERS:
- viewing_angle       : angle of the phone support above the desk (degrees)
- backrest_length     : length of the tilted phone support (mm)
- backrest_width      : width of the support frame (mm)
- base_width          : overall left–right footprint (mm)
- base_depth          : overall front–back footprint (mm)
- lip_height          : front retaining lip height above the base (mm)
- charging_notch_width: center opening for a charging cable (mm)

Demo variants: set viewing_angle=50, 65 or 80 degrees.
All 3 variants maintain the same phone-rest geometry except the viewing angle.
Tip: F5 = preview; F6 = full render; File > Export > STL.
*/

// ---------- Editable design parameters ----------
viewing_angle        = 65;     // degrees from horizontal; suggested range 50..80
backrest_length      = 132;    // mm along inclined support
backrest_width       = 88;     // mm (left to right)
backrest_thickness   = 6;      // mm
window_width         = 40;     // mm, center cutout width
window_length        = 72;     // mm, center cutout length
window_corner_radius = 7;      // mm

base_width           = 120;    // mm
base_depth           = 145;    // mm
base_thickness       = 7;      // mm
base_corner_radius   = 8;      // mm

backrest_bottom_y    = -37;    // mm, pivot location on base
lip_width            = 88;     // mm
lip_front_y          = -67;    // mm
lip_thickness        = 12;     // mm (front to back)
lip_height           = 15;     // mm above the base
charging_notch_width = 15;     // mm; set to 0 for a continuous lip

rib_thickness        = 9;      // mm, two triangular support ribs
rib_center_offset    = 32;     // mm from center left/right
rib_rear_y           = 57;     // mm
rib_attach_fraction  = 0.73;   // along the inclined backrest (0..1)
rib_beam_radius       = 6.5;    // mm, radius of diagonal support struts

$fn = 48;

assert(viewing_angle >= 45 && viewing_angle <= 85, "Angle outside tested design range");
assert(backrest_width <= base_width, "Backrest wider than base");
assert(window_width < backrest_width - 2 * rib_thickness, "Window too wide for backrest rails");
assert(window_length < backrest_length - 24, "Window too long");
assert(lip_width > charging_notch_width, "Notch must be narrower than lip");
assert(base_corner_radius * 2 < min(base_width, base_depth), "Base corner radius too large");

// An extruded polygon drawn as (y,z), with thickness in x.
module yz_extrusion(points, xcenter, thickness) {
    translate([xcenter - thickness/2, 0, 0])
        rotate([0,90,0])
            linear_extrude(height=thickness, convexity=10)
                polygon(points=[for (p=points) [-p[1], p[0]]]);
}

// Rounded footprint, lies flat on the print bed.
module footprint() {
    linear_extrude(height=base_thickness, convexity=10)
        offset(r=base_corner_radius)
            square([base_width-2*base_corner_radius,
                    base_depth-2*base_corner_radius], center=true);
}

// Tilted back support; a central window reduces material and shows angle clearly.
module backrest() {
    translate([0, backrest_bottom_y, base_thickness])
        rotate([viewing_angle,0,0])
            translate([0,backrest_length/2,0])
                difference() {
                    cube([backrest_width,backrest_length,backrest_thickness], center=true);
                    // Opening passes completely through the backrest thickness.
                    translate([0,0,-backrest_thickness])
                        linear_extrude(height=2*backrest_thickness, convexity=10)
                            offset(r=window_corner_radius)
                                square([window_width-2*window_corner_radius,
                                        window_length-2*window_corner_radius], center=true);
                }
}

// Two diagonal support struts stabilize the tilted frame without a solid wall.
module support_rib(xcenter) {
    a = viewing_angle;
    t = rib_attach_fraction * backrest_length;
    // Attach to rear face of the backrest.
    ya = backrest_bottom_y + t*cos(a) + (backrest_thickness/2)*sin(a);
    za = base_thickness + t*sin(a) - (backrest_thickness/2)*cos(a);
    translate([xcenter-rib_thickness/2,0,0])
        rotate([0,90,0])
            linear_extrude(height=rib_thickness, convexity=10)
                hull() {
                    translate([-za, ya]) circle(r=rib_beam_radius, $fn=32);
                    translate([-(base_thickness+1),rib_rear_y]) circle(r=rib_beam_radius+1, $fn=32);
                }
}

// Front stop with a cable-access notch in the middle.
module retaining_lip() {
    segment_width = (lip_width-charging_notch_width)/2;
    profile = [
        [lip_front_y,base_thickness-1],
        [lip_front_y+lip_thickness,base_thickness-1],
        [lip_front_y+lip_thickness,base_thickness+lip_height-5],
        [lip_front_y+lip_thickness-4,base_thickness+lip_height],
        [lip_front_y,base_thickness+lip_height]
    ];
    if (charging_notch_width == 0)
        yz_extrusion(profile,0,lip_width);
    else {
        yz_extrusion(profile,-(charging_notch_width+segment_width)/2,segment_width);
        yz_extrusion(profile, (charging_notch_width+segment_width)/2,segment_width);
    }
}

// This is the printed part (phone itself is intentionally not included).
union() {
    footprint();
    backrest();
    support_rib(-rib_center_offset);
    support_rib( rib_center_offset);
    retaining_lip();
}
