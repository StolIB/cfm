{-# LANGUAGE NoImplicitPrelude #-}

module RTL.Strobes where

import Clash.Prelude

import RTL.Common.Strobe

-- | Event type for the data phase of a vector fetch. This is used as @Maybe
-- VectorFetchData@ as a type isomorphic to 'Bool' but harder to mix up.
data VectorFetchData = VectorFetchData
  deriving (Eq, Show, Enum, Bounded)

instance Strobe VectorFetchData where strobeValue = VectorFetchData

-- | Event type for the address phase of a vector fetch. This is used as @Maybe
-- VectorFetchAddress@ as a type isomorphic to 'Bool' but harder to mix up.
data VectorFetchAddress = VectorFetchAddress
  deriving (Eq, Show, Enum, Bounded)

instance Strobe VectorFetchAddress where strobeValue = VectorFetchAddress

-- | Event type for the interrupt controller re-enabling interrupts due to
-- trigger.
data EnablingInterrupts = EnablingInterrupts
  deriving (Eq, Show, Enum, Bounded)
instance Strobe EnablingInterrupts where strobeValue = EnablingInterrupts
