// Editable exterior of the sample part. Units are millimetres.
outer_rounding = 3;
side_bend = 1;
top_taper = 3;

mount_height = 12;
body_height = 34;

module mount_solid() {
    translate([-50, -24, 0]) cube([100, 48, mount_height]);
}

module mating_voids() {
    for (y = [-11, 11])
        translate([-39, y, -1]) cylinder(h = mount_height + 2, r = 4.5, $fn = 48);
    translate([-51, -4, -1]) cube([16, 8, 9]);
}

module section(z, inset, radius) {
    translate([9, 0, z])
        hull()
            for (x = [-1, 1], y = [-1, 1])
                translate([x * (34 - inset - radius), y * (19 - inset - radius), 0])
                    cylinder(h = 0.2, r = radius, $fn = 32);
}

module exterior() {
    levels = [mount_height - 0.4, mount_height + 4, 22, 28, body_height - 0.2];
    insets = [0, 0, side_bend, side_bend * 0.5, top_taper];
    for (i = [0:3])
        hull() {
            section(levels[i], insets[i], outer_rounding);
            section(levels[i + 1], insets[i + 1], outer_rounding);
        }
}

difference() {
    union() {
        mount_solid();
        exterior();
    }
    mating_voids();
}
