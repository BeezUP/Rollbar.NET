﻿namespace Rollbar.PayloadStore {
    using System;
    using System.Collections.Generic;
    using System.Text;

    public class PayloadRecord
    {
        public Guid ID { get; set; } = Guid.NewGuid();

        public DateTime Timestamp { get; set; }
        public string PayloadJson { get; set; }
        public string ConfigJson { get; set; }

        public Destination Destination { get; set; }
        public Guid DestinationID { get; set; }

    }
}
