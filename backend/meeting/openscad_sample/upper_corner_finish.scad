// Add a tapered quarter-round cut on one upper outer corner. The body and
// cable channels come from the user's original cable_organizer() module.
module upper_corner_cut(sx, sy, extra) {
    if (extra > 0) {
        r = corner_radius + extra;
        z0 = 6;
        h = body_height - z0;
        assert(extra <= 2 && z0 > floor_thickness && h > 0);
        assert(r < end_margin - max(mouth_width, groove_diameter)/2);
        translate([sx*body_length/2, sy*body_depth/2, z0])
            scale([-sx, -sy, 1])
                difference() {
                    translate([-eps, -eps, 0]) cube([r+eps, r+eps, h+eps]);
                    // At z0 the cone encloses the whole square. Its top circle
                    // trims the footprint to radius r, tangent to both sides.
                    translate([r, r, 0])
                        cylinder(h=h+eps, r1=sqrt(2)*r, r2=r-(sqrt(2)-1)*r*eps/h);
                }
    }
}

module finished_cable_organizer() {
    if (max(upper_corner_px_py, upper_corner_px_ny, upper_corner_nx_py, upper_corner_nx_ny) == 0)
        cable_organizer();
    else difference() {
        cable_organizer();
        upper_corner_cut(1, 1, upper_corner_px_py);
        upper_corner_cut(1, -1, upper_corner_px_ny);
        upper_corner_cut(-1, 1, upper_corner_nx_py);
        upper_corner_cut(-1, -1, upper_corner_nx_ny);
    }
}
