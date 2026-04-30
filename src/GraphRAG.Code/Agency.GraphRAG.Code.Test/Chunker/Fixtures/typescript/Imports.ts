import React, { useMemo as memo, type FC } from "react";
import * as utils from "./utils";
import "zone.js";

export const Imports = () => memo(() => utils.value, []);
