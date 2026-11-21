// Cable organizer | parametric prototype, millimeters
// Open top: push a cable down into each through-groove.
// F5 preview, F6 render, then File > Export > Export as STL.

/* [Cable slots] */
slot_count = 5;           // 홈 개수
slot_pitch = 16.5;        // 홈 사이 거리
groove_diameter = 5.6;    
    // 홈 안쪽 입구 너비
throat_width = 4.2;       // 홈 위쪽 입구 너비
mouth_width = 6.0;        // 맨 위쪽 입구의 너비
entry_chamfer_height = 1.0; // [0.5:0.1:2]

/* [Body] */
end_margin = 9;           // 마지막 홈에서 양옆까지 길이
body_depth = 18;          // Body 두께
body_height = 10;         // Body 높이
floor_thickness = 3.8;    // 홈의 높이
corner_radius = 3;        // 꼭짓점의 곡률

/* [Hidden] */
$fn = 96;
eps = 0.05;
body_length = (slot_count - 1)*slot_pitch + 2*end_margin;
channel_z = floor_thickness + groove_diameter/2;
cut_length = body_depth + 2;

assert(slot_count >= 1 && slot_count == floor(slot_count));
assert(groove_diameter > 0 && throat_width > 0 && mouth_width >= throat_width);
assert(slot_pitch > max(groove_diameter,mouth_width)+1, "Increase spacing between slots.");
assert(end_margin > max(groove_diameter,mouth_width)/2+1, "Increase end margin.");
assert(body_height > channel_z, "Increase body height or reduce floor/groove size.");
assert(body_height-entry_chamfer_height > channel_z, "Entry chamfer too deep.");
assert(corner_radius >= 0 && 2*corner_radius < min(body_length,body_depth));

module rounded_body() {
    linear_extrude(height=body_height)
        if (corner_radius == 0)
            square([body_length,body_depth],center=true);
        else
            offset(r=corner_radius)
                square([body_length-2*corner_radius,body_depth-2*corner_radius],center=true);
}

module cable_channel() {
    // Circular cable seat, oriented through the front and back.
    translate([0,0,channel_z])
        rotate([90,0,0])
            cylinder(d=groove_diameter,h=cut_length,center=true);
    // Straight throat, open at the top.
    translate([-throat_width/2,-cut_length/2,channel_z-eps])
        cube([throat_width,cut_length,body_height-channel_z+2*eps]);
    // Flared entry: makes inserting a cable easier.
    rotate([90,0,0])
        linear_extrude(height=cut_length,center=true)
            polygon(points=[
                [-throat_width/2,body_height-entry_chamfer_height],
                [ throat_width/2,body_height-entry_chamfer_height],
                [ mouth_width/2,body_height+eps],
                [-mouth_width/2,body_height+eps]
            ]);
}

module cable_organizer() {
    difference() {
        rounded_body();
        for (i=[0:slot_count-1])
            translate([(i-(slot_count-1)/2)*slot_pitch,0,0])
                cable_channel();
    }
}

cable_organizer();
