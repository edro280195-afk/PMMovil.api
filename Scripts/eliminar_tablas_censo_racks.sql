-- MySQL: elimina únicamente los datos y tablas del censo de racks.
-- warehouse_layouts y las demás tablas del mapa de bodega se conservan.

DROP TABLE IF EXISTS `rack_census_findings`;
DROP TABLE IF EXISTS `rack_census_observations`;
DROP TABLE IF EXISTS `rack_census_snapshot_items`;
DROP TABLE IF EXISTS `rack_census_sessions`;
